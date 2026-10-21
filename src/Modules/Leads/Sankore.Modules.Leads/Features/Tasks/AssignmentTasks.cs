using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Leads.Domain;
using Sankore.Modules.Leads.Infrastructure;

namespace Sankore.Modules.Leads.Features.Tasks;

/// <summary>
/// The tasks that follow a lead's assignment: created when a lead first reaches an agent,
/// TRANSFERRED when it moves to another one, extended when its SLA is escalated, completed when the
/// first contact is made. One place, because several code paths touch them (dispatch, SLA
/// escalation, first contact declared by the agent, first contact from a qualified call) and each
/// used to know only its own part — a lead reassigned left its old task open on the previous
/// agent's "Mes tâches", and a first contact never closed anything.
///
/// <para>
/// A reassignment used to CANCEL the previous agent's task and create a fresh one for the new
/// agent: the work was the same, but its history was split across two rows and nobody could tell
/// who had held it before, nor against which deadline. The task is now one row that changes hands,
/// and every change leaves a <see cref="TaskReassignment"/> carrying both deadlines.
/// </para>
///
/// <para>
/// Every query here filters the tenant EXPLICITLY and ignores the query filters: the dispatch
/// consumer has no HTTP context, so the ambient tenant is not the one being processed.
/// </para>
/// </summary>
internal static class AssignmentTasks
{
    private static readonly CrmTaskStatus[] Open = [CrmTaskStatus.Pending, CrmTaskStatus.InProgress];

    /// <summary>
    /// Brings the lead's assignment tasks in line with its new agent, and returns how many were
    /// created. The caller saves.
    /// </summary>
    /// <param name="actorId">The manager who moved the lead by hand; null when the system did.</param>
    /// <param name="reason">Their stated reason, written into the transferred tasks' history.</param>
    /// <param name="rules">
    /// The tenant's active <c>LeadDispatched</c> generation rules. None configured — the common case —
    /// gives the default first-contact task, so an assignment is never silent on the agent's list.
    /// </param>
    public static async Task<int> SyncOnAssignedAsync(
        LeadsDbContext db,
        Guid tenantId,
        Guid leadId,
        Guid agentId,
        DateTimeOffset slaDeadline,
        IReadOnlyList<TaskGenerationRule> rules,
        DateTimeOffset now,
        CancellationToken ct,
        Guid? actorId = null,
        string? reason = null)
    {
        var openTasks = await db.CrmTasks
            .IgnoreQueryFilters()
            .AsTracking()
            .Where(t => t.TenantId == tenantId
                     && t.LeadId == leadId
                     && t.TriggerEventType == TaskTriggerEvents.LeadDispatched
                     && Open.Contains(t.Status))
            .ToListAsync(ct);

        var alreadyMine = openTasks.Where(t => t.AssignedAgentId == agentId).ToList();

        // The lead left those agents: their "call this lead" tasks follow it to the new one. A
        // duplicate of a task the new agent already holds — legacy rows from before the transfer —
        // is cancelled instead, so the agent does not end up with the same work twice.
        foreach (var task in openTasks.Where(t => t.AssignedAgentId != agentId))
        {
            if (alreadyMine.Any(t => t.Type == task.Type))
            {
                task.Cancel();
                continue;
            }

            // Rescheduled exactly as a task created now would be: the matching rule's durations,
            // else the assignment's own deadline.
            var rule = rules.FirstOrDefault(r => r.TaskType == task.Type);
            var newSla = rule is null ? slaDeadline : now.Add(rule.SlaDuration);
            var newDue = rule is null ? slaDeadline : now.Add(rule.DueDuration);
            var slaBefore = task.SlaDeadline;

            var moved = task.Reassign(agentId, newSla, newDue);
            if (moved.IsFailure) continue;

            db.TaskReassignments.Add(TaskReassignment.Create(
                tenantId:            tenantId,
                taskId:              task.Id,
                previousAgentId:     moved.Value,
                newAgentId:          agentId,
                reason:              TransferReason(reason),
                actorId:             actorId,
                slaExtended:         newSla != slaBefore,
                newSlaDeadline:      newSla,
                previousSlaDeadline: slaBefore,
                reassignedAt:        now));
            alreadyMine.Add(task);
        }

        // The lead found an agent: the "nobody could take it, place it by hand" tasks are done,
        // whether the queue, the engine or a manager placed it.
        var waiting = await db.CrmTasks
            .IgnoreQueryFilters()
            .AsTracking()
            .Where(t => t.TenantId == tenantId
                     && t.LeadId == leadId
                     && t.TriggerEventType == TaskTriggerEvents.LeadDispatchingFailed
                     && Open.Contains(t.Status))
            .ToListAsync(ct);
        foreach (var task in waiting)
            task.Complete();

        var lead = await db.Leads
            .IgnoreQueryFilters()
            .Where(l => l.TenantId == tenantId && l.Id == leadId)
            .Select(l => new { l.FullName })
            .FirstOrDefaultAsync(ct);
        var leadName = string.IsNullOrWhiteSpace(lead?.FullName) ? "lead" : lead!.FullName;

        var created = 0;

        if (rules.Count == 0)
        {
            // Idempotent on (lead, agent, type): a redelivered dispatch event, or the same agent
            // dispatched twice, must not stack a second identical task.
            if (alreadyMine.Any(t => t.Type == CrmTaskType.FirstContact))
                return created;

            db.CrmTasks.Add(CrmTask.Create(
                tenantId: tenantId,
                type: CrmTaskType.FirstContact,
                priority: CrmTaskPriority.High,
                title: $"Premier contact — {leadName}",
                // Due at the assignment's own SLA deadline: the task and the SLA tell the agent
                // the same thing, instead of a fixed delay that disagrees with the dispatch rule.
                dueAt: slaDeadline,
                leadId: leadId,
                assignedAgentId: agentId,
                slaDeadline: slaDeadline,
                description: "Ce lead vient de vous être affecté. Contactez-le avant l’échéance ; "
                             + "la tâche se termine d’elle-même quand le premier contact est enregistré.",
                triggerEventType: TaskTriggerEvents.LeadDispatched,
                triggerEventId: leadId));
            return created + 1;
        }

        foreach (var rule in rules)
        {
            if (alreadyMine.Any(t => t.Type == rule.TaskType))
                continue;

            db.CrmTasks.Add(CrmTask.Create(
                tenantId: tenantId,
                type: rule.TaskType,
                priority: rule.Priority,
                title: Render(rule.TitleTemplate, leadId, leadName),
                dueAt: now.Add(rule.DueDuration),
                leadId: leadId,
                assignedAgentId: agentId,
                slaDeadline: now.Add(rule.SlaDuration),
                description: rule.DescriptionTemplate is null ? null : Render(rule.DescriptionTemplate, leadId, leadName),
                triggerEventType: TaskTriggerEvents.LeadDispatched,
                triggerEventId: leadId));
            created++;
        }

        return created;
    }

    /// <summary>
    /// The first contact was made: the agent's open first-contact task for this lead is done. The
    /// caller saves. Only the assignment's first-contact tasks — a follow-up the agent created by
    /// hand is not "the first call" and stays open.
    /// </summary>
    public static async Task CompleteFirstContactAsync(
        LeadsDbContext db, Guid tenantId, Guid leadId, Guid agentId, CancellationToken ct)
    {
        var tasks = await db.CrmTasks
            .IgnoreQueryFilters()
            .AsTracking()
            .Where(t => t.TenantId == tenantId
                     && t.LeadId == leadId
                     && t.AssignedAgentId == agentId
                     && t.Type == CrmTaskType.FirstContact
                     && t.TriggerEventType == TaskTriggerEvents.LeadDispatched
                     && Open.Contains(t.Status))
            .ToListAsync(ct);

        foreach (var task in tasks)
            task.Complete();
    }

    /// <summary>
    /// The lead's first-contact SLA was escalated: the agent keeps their open assignment tasks, and
    /// those whose deadline has passed take the escalated one. Each extension is recorded in the
    /// task's attribution history with both deadlines. The caller saves; returns how many tasks
    /// were extended.
    /// </summary>
    public static async Task<int> ExtendOnEscalationAsync(
        LeadsDbContext db,
        Guid tenantId,
        Guid leadId,
        Guid agentId,
        DateTimeOffset newSlaDeadline,
        int escalationDepth,
        DateTimeOffset now,
        CancellationToken ct)
    {
        // Only the overdue ones: a task whose own deadline is still ahead was not what breached,
        // and moving it would shorten or lengthen a delay nobody escalated.
        var overdue = await db.CrmTasks
            .IgnoreQueryFilters()
            .AsTracking()
            .Where(t => t.TenantId == tenantId
                     && t.LeadId == leadId
                     && t.AssignedAgentId == agentId
                     && t.TriggerEventType == TaskTriggerEvents.LeadDispatched
                     && Open.Contains(t.Status)
                     && t.SlaDeadline != null
                     && t.SlaDeadline <= now)
            .ToListAsync(ct);

        var extended = 0;
        foreach (var task in overdue)
        {
            var slaBefore = task.SlaDeadline;
            if (task.ExtendSla(newSlaDeadline).IsFailure) continue;

            db.TaskReassignments.Add(TaskReassignment.Create(
                tenantId:            tenantId,
                taskId:              task.Id,
                previousAgentId:     agentId,
                newAgentId:          agentId,
                reason:              $"Escalade SLA niveau {escalationDepth} — premier contact non enregistré",
                actorId:             null,
                slaExtended:         true,
                newSlaDeadline:      newSlaDeadline,
                previousSlaDeadline: slaBefore,
                reassignedAt:        now));
            extended++;
        }

        return extended;
    }

    /// <summary>
    /// The manager's reason may already use the whole 500 characters the dispatch validator allows,
    /// which is also the column's width — cut rather than fail the transfer on the prefix.
    /// </summary>
    private static string TransferReason(string? reason)
    {
        var text = string.IsNullOrWhiteSpace(reason) ? "Lead réattribué" : $"Lead réattribué — {reason.Trim()}";
        return text.Length <= 500 ? text : text[..500];
    }

    /// <summary><c>{LeadId}</c> was the only placeholder; <c>{LeadName}</c> reads better on a task list.</summary>
    private static string Render(string template, Guid leadId, string leadName) =>
        template.Replace("{LeadId}", leadId.ToString()).Replace("{LeadName}", leadName);
}
