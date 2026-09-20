namespace Sankore.Modules.Leads.Features.Tasks.CompleteTask;

using MassTransit;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Leads.Features.DispatchLead;
using Sankore.Modules.Leads.Infrastructure;
using Sankore.Shared.Infrastructure.Auth;
using Sankore.Shared.Kernel;

internal sealed class CompleteTaskHandler(
    LeadsDbContext db,
    AgentCapacityService capacityService,
    IBus bus,
    ICurrentUser currentUser)
    : IRequestHandler<CompleteTaskCommand, Result>
{
    public async Task<Result> Handle(CompleteTaskCommand cmd, CancellationToken ct)
    {
        var task = await db.CrmTasks.AsTracking()
            .FirstOrDefaultAsync(t => t.Id == cmd.TaskId, ct);

        if (task is null)
            return Result.Fail("TASK_NOT_FOUND");

        // Ownership guard: only the assigned agent or a supervisor can complete
        if (task.AssignedAgentId.HasValue
            && task.AssignedAgentId.Value != currentUser.Id
            && !IsSupervisor())
        {
            return Result.Fail("NOT_TASK_OWNER");
        }

        var agentId  = task.AssignedAgentId;
        var tenantId = task.TenantId;

        var result = task.Complete();
        if (result.IsFailure) return result;

        await db.SaveChangesAsync(ct);

        if (agentId.HasValue)
            await capacityService.InvalidateAsync(tenantId, agentId.Value, ct);

        // Publish event for downstream rule evaluation (US-M13-093)
        await bus.Publish(new TaskCompletedEvent(
            TaskId:          task.Id,
            TenantId:        tenantId,
            LeadId:          task.LeadId,
            AssignedAgentId: agentId,
            TaskType:        task.Type.ToString()), ct);

        return Result.Ok();
    }

    private bool IsSupervisor() =>
        currentUser.Roles.Any(r =>
            r is "System" or "Administrator" or "SalesManager" or "BranchManager");
}
