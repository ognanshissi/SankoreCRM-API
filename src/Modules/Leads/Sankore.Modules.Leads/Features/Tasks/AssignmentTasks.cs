using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Leads.Domain;
using Sankore.Modules.Leads.Infrastructure;

namespace Sankore.Modules.Leads.Features.Tasks;

/// <summary>
/// The tasks that follow a lead's assignment: created when a lead reaches an agent, cancelled when
/// it leaves them, completed when the first contact is made. One place, because three code paths
/// touch them (dispatch, first contact declared by the agent, first contact from a qualified call)
/// and each used to know only its own part — a lead reassigned left its old task open on the
/// previous agent's "Mes tâches", and a first contact never closed anything.
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
        CancellationToken ct)
    {
        var openTasks = await db.CrmTasks
            .IgnoreQueryFilters()
            .AsTracking()
            .Where(t => t.TenantId == tenantId
                     && t.LeadId == leadId
                     && t.TriggerEventType == TaskTriggerEvents.LeadDispatched
                     && Open.Contains(t.Status))
            .ToListAsync(ct);

        // The lead left those agents: their "call this lead" tasks are no longer theirs to do.
        foreach (var stale in openTasks.Where(t => t.AssignedAgentId != agentId))
            stale.Cancel();

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

        var alreadyMine = openTasks.Where(t => t.AssignedAgentId == agentId).ToList();

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

    /// <summary><c>{LeadId}</c> was the only placeholder; <c>{LeadName}</c> reads better on a task list.</summary>
    private static string Render(string template, Guid leadId, string leadName) =>
        template.Replace("{LeadId}", leadId.ToString()).Replace("{LeadName}", leadName);
}
