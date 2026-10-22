namespace Sankore.Modules.Leads.Features.Tasks.ReassignTask;

using MassTransit;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Sankore.Modules.Leads.Domain;
using Sankore.Modules.Leads.Features.DispatchLead;
using Sankore.Modules.Leads.Features.Tasks.Collaboration;
using Sankore.Modules.Leads.Infrastructure;
using Sankore.Shared.Infrastructure.Messaging;
using Sankore.Shared.Kernel;

internal sealed class ReassignTaskHandler(
    LeadsDbContext db,
    [FromKeyedServices(nameof(LeadsDbContext))] IEventPublisher publisher,
    AgentCapacityService capacityService,
    IBus bus,
    ILogger<ReassignTaskHandler> logger,
    TaskAccess access)
    : IRequestHandler<ReassignTaskCommand, Result>
{
    public async Task<Result> Handle(ReassignTaskCommand cmd, CancellationToken ct)
    {
        var loaded = await access.LoadAsync(cmd.TaskId, tracking: true, ct);
        if (loaded.IsFailure)
            return Result.Fail(loaded.Error!);
        var task = loaded.Value!;

        if (task.AssignedAgentId == cmd.NewAgentId)
            return Result.Fail("ALREADY_ASSIGNED_TO_AGENT");

        // Compute optional SLA extension
        DateTimeOffset? newSlaDeadline = cmd.SlaExtensionHours.HasValue
            ? DateTimeOffset.UtcNow.AddHours(cmd.SlaExtensionHours.Value)
            : null;

        var slaBefore      = task.SlaDeadline;
        var reassignResult = task.Reassign(cmd.NewAgentId, newSlaDeadline);
        if (reassignResult.IsFailure)
            return Result.Fail(reassignResult.Error!);

        var previousAgentId = reassignResult.Value;
        var slaExtended     = newSlaDeadline.HasValue;

        // Persist audit record in the same transaction
        var history = TaskReassignment.Create(
            tenantId:       task.TenantId,
            taskId:         task.Id,
            previousAgentId: previousAgentId,
            newAgentId:     cmd.NewAgentId,
            reason:         cmd.Reason,
            actorId:        cmd.ActorId,
            slaExtended:    slaExtended,
            newSlaDeadline: newSlaDeadline,
            previousSlaDeadline: slaBefore);

        db.TaskReassignments.Add(history);

        var evt = new TaskReassignedEvent(
            TaskId:         task.Id,
            TenantId:       task.TenantId,
            PreviousAgentId: previousAgentId,
            NewAgentId:     cmd.NewAgentId,
            Reason:         cmd.Reason,
            ActorId:        cmd.ActorId,
            ReassignedAt:   history.ReassignedAt,
            SlaExtended:    slaExtended,
            NewSlaDeadline: newSlaDeadline);

        await publisher.PublishAsync(evt, ct);
        await db.SaveChangesAsync(ct);

        // Invalidate capacity cache for both agents
        if (previousAgentId.HasValue)
            await capacityService.InvalidateAsync(task.TenantId, previousAgentId.Value, ct);
        await capacityService.InvalidateAsync(task.TenantId, cmd.NewAgentId, ct);

        await bus.Publish(new Tasks.Events.TaskAssignedIntegrationEvent(
            TaskId:   task.Id,
            TenantId: task.TenantId,
            AgentId:  cmd.NewAgentId,
            Title:    task.Title,
            DueAt:    task.DueAt,
            LeadId:   task.LeadId), ct);

        logger.LogInformation(
            "Task {TaskId} reassigned from {PreviousAgent} to {NewAgent} by {Actor} (reason: {Reason})",
            task.Id, previousAgentId, cmd.NewAgentId, cmd.ActorId?.ToString() ?? "SYSTEM", cmd.Reason);

        return Result.Ok();
    }
}
