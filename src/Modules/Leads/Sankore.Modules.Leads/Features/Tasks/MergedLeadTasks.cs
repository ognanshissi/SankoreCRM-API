namespace Sankore.Modules.Leads.Features.Tasks;

using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Leads.Domain;
using Sankore.Modules.Leads.Infrastructure;

/// <summary>
/// A merge archives the duplicate, and its open tasks follow the person to the lead that is kept
/// (product owner, 2026-11-05): they used to stay open on an archived lead nobody opens any more.
/// Only tasks ABOUT the lead, as <see cref="ArchivedLeadTasks"/>. The caller saves.
///
/// <list type="bullet">
/// <item>The kept lead's holder takes them, with a history line and the same deadlines: the agent of
/// the duplicate may no longer open the lead. A kept lead nobody holds leaves them where they are.</item>
/// <item>An assignment task the kept lead already has is cancelled, not doubled, as
/// <see cref="AssignmentTasks"/> does; a « Premier contact » too once the kept lead was contacted, for
/// nothing would ever close it.</item>
/// <item>A « dispatch by hand » task only moves when the kept lead still waits for an agent.</item>
/// </list>
/// </summary>
internal static class MergedLeadTasks
{
    private const string Reason = "Fusion de leads : la tâche suit le lead conservé";
    private static readonly CrmTaskStatus[] Open = [CrmTaskStatus.Pending, CrmTaskStatus.InProgress];

    /// <returns>The agents whose load changed, for the capacity cache AFTER the save.</returns>
    public static async Task<IReadOnlyCollection<Guid>> MoveOpenAsync(
        LeadsDbContext db, Lead source, Lead target, Guid actorId, DateTimeOffset now, CancellationToken ct)
    {
        var tasks = await db.CrmTasks.AsTracking()
            .Where(t => t.LeadId == source.Id && Open.Contains(t.Status))
            .ToListAsync(ct);
        if (tasks.Count == 0) return [];

        var alreadyOnTarget = await db.CrmTasks
            .Where(t => t.LeadId == target.Id && Open.Contains(t.Status))
            .Select(t => new { t.Type, t.TriggerEventType })
            .ToListAsync(ct);

        var holder = target.CurrentAssignedId ?? target.OwnerId;
        var agents = new HashSet<Guid>();

        foreach (var task in tasks)
        {
            var isAssignmentTask = task.TriggerEventType == TaskTriggerEvents.LeadDispatched;
            var isDispatchByHand = task.TriggerEventType == TaskTriggerEvents.LeadDispatchingFailed;
            var doubled = (isAssignmentTask || isDispatchByHand)
                && alreadyOnTarget.Any(t => t.Type == task.Type && t.TriggerEventType == task.TriggerEventType);
            var pointless = (isAssignmentTask && task.Type == CrmTaskType.FirstContact && target.Status != LeadStatus.New)
                || (isDispatchByHand && target.CurrentAssignmentId is not null);

            if (doubled || pointless)
            {
                task.Cancel();
                if (task.AssignedAgentId is { } agent) agents.Add(agent);
                continue;
            }

            task.MoveToLead(target.Id);

            if (isDispatchByHand || holder is not { } newAgent || task.AssignedAgentId == newAgent)
                continue;

            var moved = task.Reassign(newAgent);
            if (moved.IsFailure) continue;

            db.TaskReassignments.Add(TaskReassignment.Create(
                tenantId:            task.TenantId,
                taskId:              task.Id,
                previousAgentId:     moved.Value,
                newAgentId:          newAgent,
                reason:              Reason,
                actorId:             actorId,
                newSlaDeadline:      task.SlaDeadline,
                previousSlaDeadline: task.SlaDeadline,
                reassignedAt:        now));

            agents.Add(newAgent);
            if (moved.Value is { } previous) agents.Add(previous);
        }

        return agents;
    }
}
