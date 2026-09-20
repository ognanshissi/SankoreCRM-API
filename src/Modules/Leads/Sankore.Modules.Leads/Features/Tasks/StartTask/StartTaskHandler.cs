namespace Sankore.Modules.Leads.Features.Tasks.StartTask;

using MediatR;
using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Leads.Features.DispatchLead;
using Sankore.Modules.Leads.Infrastructure;
using Sankore.Shared.Infrastructure.Auth;
using Sankore.Shared.Kernel;

internal sealed class StartTaskHandler(
    LeadsDbContext db,
    AgentCapacityService capacityService,
    ICurrentUser currentUser)
    : IRequestHandler<StartTaskCommand, Result>
{
    public async Task<Result> Handle(StartTaskCommand cmd, CancellationToken ct)
    {
        var task = await db.CrmTasks.AsTracking()
            .FirstOrDefaultAsync(t => t.Id == cmd.TaskId, ct);

        if (task is null)
            return Result.Fail("TASK_NOT_FOUND");

        // Ownership guard: only the assigned agent or a supervisor can start
        if (task.AssignedAgentId.HasValue
            && task.AssignedAgentId.Value != currentUser.Id
            && !IsSupervisor())
        {
            return Result.Fail("NOT_TASK_OWNER");
        }

        var result = task.StartProgress();
        if (result.IsFailure) return result;

        await db.SaveChangesAsync(ct);
        return Result.Ok();
    }

    private bool IsSupervisor() =>
        currentUser.Roles.Any(r =>
            r is "System" or "Administrator" or "SalesManager" or "BranchManager");
}
