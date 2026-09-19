namespace Sankore.Modules.Leads.Features.Tasks.DeclineTask;

using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Sankore.Modules.Leads.Domain;
using Sankore.Modules.Leads.Features.DispatchLead;
using Sankore.Modules.Leads.Features.Tasks.DispatchTask;
using Sankore.Modules.Leads.Infrastructure;
using Sankore.Shared.Infrastructure.Messaging;
using Sankore.Shared.Kernel;

/// <summary>
/// Orchestrates US-M13-084: validate decline, unassign the agent, persist
/// the audit record, add a temporary exclusion entry, invalidate capacity
/// caches, publish the event, and trigger automatic re-dispatch.
/// </summary>
internal sealed class DeclineTaskHandler(
    LeadsDbContext db,
    [FromKeyedServices(nameof(LeadsDbContext))] IEventPublisher publisher,
    AgentCapacityService capacityService,
    AgentExclusionService exclusionService,
    ISender sender,
    ILogger<DeclineTaskHandler> logger)
    : IRequestHandler<DeclineTaskCommand, Result>
{
    public async Task<Result> Handle(DeclineTaskCommand cmd, CancellationToken ct)
    {
        // 1. Load and validate
        var task = await db.CrmTasks.AsTracking()
            .FirstOrDefaultAsync(t => t.Id == cmd.TaskId, ct);

        if (task is null)
            return Result.Fail("TASK_NOT_FOUND");

        if (task.AssignedAgentId != cmd.AgentId)
            return Result.Fail("AGENT_NOT_ASSIGNED_TO_TASK");

        // 2. Domain transition — unassign + reset to Pending
        var declineResult = task.Decline();
        if (declineResult.IsFailure)
            return Result.Fail(declineResult.Error!);

        var declinedAgentId = declineResult.Value;

        // 3. Persist audit record
        var decline = TaskDecline.Create(
            tenantId: task.TenantId,
            taskId:   task.Id,
            agentId:  declinedAgentId,
            reason:   cmd.Reason);

        db.TaskDeclines.Add(decline);

        // 4. Publish integration event
        await publisher.PublishAsync(
            new TaskDeclinedEvent(
                TaskId:     task.Id,
                TenantId:   task.TenantId,
                AgentId:    declinedAgentId,
                Reason:     cmd.Reason,
                DeclinedAt: decline.DeclinedAt),
            ct);

        await db.SaveChangesAsync(ct);

        // 5. Invalidate capacity cache — agent now holds one fewer open task
        await capacityService.InvalidateAsync(task.TenantId, declinedAgentId, ct);

        // 6. Add temporary exclusion so re-dispatch skips this agent (TTL from rules)
        var rules = await db.DispatchingRules
            .Where(r => r.IsActive)
            .OrderByDescending(r => r.Priority)
            .FirstOrDefaultAsync(ct)
            ?? DispatchingRule.Default();

        await exclusionService.ExcludeAsync(
            task.TenantId, task.Id, declinedAgentId, rules.DeclineExclusionTtl, ct);

        logger.LogInformation(
            "Task {TaskId} declined by agent {AgentId} (reason: {Reason}, exclusion TTL: {Ttl})",
            task.Id, declinedAgentId, cmd.Reason, rules.DeclineExclusionTtl);

        // 7. Auto re-dispatch — only if the task is tied to a lead (scorer needs context)
        if (task.LeadId is not null)
        {
            var redispatch = await sender.Send(
                new DispatchTaskCommand(task.Id, task.TenantId), ct);

            if (redispatch.IsFailure)
            {
                logger.LogWarning(
                    "Task {TaskId} could not be auto-redispatched after decline: {Error}",
                    task.Id, redispatch.Error);
            }
        }

        return Result.Ok();
    }
}
