namespace Sankore.Modules.Leads.Features.Tasks.GetTaskAssignments;

using MediatR;
using Sankore.Shared.Kernel;

internal sealed record GetTaskAssignmentsQuery(Guid TaskId)
    : IRequest<Result<IReadOnlyList<TaskAssignmentEntryDto>>>;

/// <summary>
/// One step of a task's attribution history, oldest first.
/// </summary>
/// <param name="Kind">
/// <c>Initial</c> — the task's creation; <c>Reassigned</c> — it changed hands;
/// <c>SlaExtended</c> — same agent, new deadline (an SLA escalation, or a manager extending it).
/// </param>
/// <param name="AgentId">Who holds the task from <paramref name="At"/> on. Null for a task created unassigned.</param>
/// <param name="SlaDeadline">
/// The task's deadline from <paramref name="At"/> on. Null when the task has none, and on steps
/// recorded before the deadline was kept in the history.
/// </param>
/// <param name="ActorId">Who made the change; null for the system (dispatch, SLA escalation) and on the initial step.</param>
public sealed record TaskAssignmentEntryDto(
    DateTimeOffset At,
    string Kind,
    Guid? PreviousAgentId,
    Guid? AgentId,
    DateTimeOffset? PreviousSlaDeadline,
    DateTimeOffset? SlaDeadline,
    string? Reason,
    Guid? ActorId);
