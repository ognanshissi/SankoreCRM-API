namespace Sankore.Modules.Leads.Features.Tasks.GetTaskAssignments;

using MediatR;
using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Leads.Features.Tasks.Collaboration;
using Sankore.Modules.Leads.Infrastructure;
using Sankore.Shared.Kernel;

/// <summary>
/// The task's attribution history: its creation, then every <c>TaskReassignment</c> row.
///
/// <para>
/// The initial step is reconstructed rather than stored — a task is created by half a dozen paths
/// and none of them writes a history row. Its agent and deadline are read off the FIRST change
/// (what the task was before it), else off the task itself when it never changed.
/// </para>
/// </summary>
internal sealed class GetTaskAssignmentsHandler(LeadsDbContext db, TaskAccess access)
    : IRequestHandler<GetTaskAssignmentsQuery, Result<IReadOnlyList<TaskAssignmentEntryDto>>>
{
    public async Task<Result<IReadOnlyList<TaskAssignmentEntryDto>>> Handle(
        GetTaskAssignmentsQuery query, CancellationToken ct)
    {
        var loaded = await access.LoadAsync(query.TaskId, tracking: false, ct);
        if (loaded.IsFailure)
            return Result.Fail<IReadOnlyList<TaskAssignmentEntryDto>>(loaded.Error!);
        var task = loaded.Value!;

        var changes = await db.TaskReassignments
            .AsNoTracking()
            .Where(r => r.TenantId == task.TenantId && r.TaskId == task.Id)
            .OrderBy(r => r.ReassignedAt)
            .ToListAsync(ct);

        var first = changes.FirstOrDefault();
        var history = new List<TaskAssignmentEntryDto>(changes.Count + 1)
        {
            new(
                At: task.CreatedAt,
                Kind: "Initial",
                PreviousAgentId: null,
                AgentId: first is null ? task.AssignedAgentId : first.PreviousAgentId,
                PreviousSlaDeadline: null,
                SlaDeadline: first is null ? task.SlaDeadline : first.PreviousSlaDeadline,
                Reason: null,
                ActorId: null),
        };

        history.AddRange(changes.Select(r => new TaskAssignmentEntryDto(
            At: r.ReassignedAt,
            Kind: r.PreviousAgentId == r.NewAgentId ? "SlaExtended" : "Reassigned",
            PreviousAgentId: r.PreviousAgentId,
            AgentId: r.NewAgentId,
            PreviousSlaDeadline: r.PreviousSlaDeadline,
            // An unchanged deadline is recorded as "no new one": the task kept the previous.
            SlaDeadline: r.NewSlaDeadline ?? r.PreviousSlaDeadline,
            Reason: r.Reason,
            ActorId: r.ActorId)));

        return Result.Ok<IReadOnlyList<TaskAssignmentEntryDto>>(history);
    }
}
