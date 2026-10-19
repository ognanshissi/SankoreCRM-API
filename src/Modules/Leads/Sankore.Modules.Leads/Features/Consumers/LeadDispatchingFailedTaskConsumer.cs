namespace Sankore.Modules.Leads.Features.Consumers;

using MassTransit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Sankore.Modules.Administration.PublicApi;
using Sankore.Modules.Leads.Domain;
using Sankore.Modules.Leads.Features.DispatchLead.Events;
using Sankore.Modules.Leads.Infrastructure;

/// <summary>
/// Tells somebody that a lead entered the dispatch queue. The lead itself is retried by
/// <c>DispatchWaitingLeadsJob</c>; this is the human side — a branch manager may place it by hand
/// rather than wait for an agent to free up.
///
/// <para>
/// One task per active <c>LeadDispatchingFailed</c> generation rule when the tenant configured
/// some (unassigned, as before). Otherwise — the default — a "Dispatch manuel" task for the head of
/// the lead's agency, on their "Mes tâches". It used to do nothing at all without rules, which made
/// the event pointless even once it was actually published.
/// </para>
///
/// <para>
/// A lead with no agency, or whose agency has no active head, gets no task: there is nobody whose
/// job it is, and inventing a recipient would hand one person every branch's leftovers. It still
/// waits in the queue and leaves it on its own when an agent becomes available. The task closes by
/// itself on assignment (<c>AssignmentTasks.SyncOnAssignedAsync</c>), whoever made it.
/// </para>
///
/// Note: no ITenantContext available in MassTransit consumers (no HTTP context);
/// uses IgnoreQueryFilters() + manual TenantId filter from the event payload.
/// </summary>
public sealed class LeadDispatchingFailedTaskConsumer(
    LeadsDbContext db,
    IAdministrationModule administration,
    ILogger<LeadDispatchingFailedTaskConsumer> logger)
    : IConsumer<LeadDispatchingFailedEvent>
{
    private static readonly CrmTaskStatus[] Open = [CrmTaskStatus.Pending, CrmTaskStatus.InProgress];

    public async Task Consume(ConsumeContext<LeadDispatchingFailedEvent> context)
    {
        var evt = context.Message;
        var ct = context.CancellationToken;
        var now = DateTimeOffset.UtcNow;

        // Idempotent per lead: a redelivered event, or a lead that left the queue and came back
        // while its first task is still open, must not stack a second one.
        var alreadyOpen = await db.CrmTasks
            .IgnoreQueryFilters()
            .AnyAsync(t => t.TenantId == evt.TenantId
                        && t.LeadId == evt.LeadId
                        && t.TriggerEventType == TaskTriggerEvents.LeadDispatchingFailed
                        && Open.Contains(t.Status), ct);
        if (alreadyOpen) return;

        var lead = await db.Leads
            .IgnoreQueryFilters()
            .Where(l => l.TenantId == evt.TenantId && l.Id == evt.LeadId)
            .Select(l => new { l.FullName, l.PreferredAgencyId, l.AgencyId, l.CurrentAssignmentId })
            .FirstOrDefaultAsync(ct);

        // Placed between the failure and this delivery — nothing left to ask anyone.
        if (lead is null || lead.CurrentAssignmentId is not null) return;

        var rules = await db.TaskGenerationRules
            .IgnoreQueryFilters()
            .Where(r => r.TenantId == evt.TenantId
                     && r.TriggerEventType == TaskTriggerEvents.LeadDispatchingFailed
                     && r.IsActive)
            .ToListAsync(ct);

        var leadName = string.IsNullOrWhiteSpace(lead.FullName) ? "lead" : lead.FullName;

        if (rules.Count > 0)
        {
            foreach (var rule in rules)
            {
                db.CrmTasks.Add(CrmTask.Create(
                    tenantId:         evt.TenantId,
                    type:             rule.TaskType,
                    priority:         rule.Priority,
                    title:            Render(rule.TitleTemplate, evt.LeadId, leadName),
                    dueAt:            now.Add(rule.DueDuration),
                    leadId:           evt.LeadId,
                    slaDeadline:      now.Add(rule.SlaDuration),
                    description:      rule.DescriptionTemplate is null ? null : Render(rule.DescriptionTemplate, evt.LeadId, leadName),
                    triggerEventType: TaskTriggerEvents.LeadDispatchingFailed,
                    triggerEventId:   evt.LeadId));
            }

            await db.SaveChangesAsync(ct);
            logger.LogInformation(
                "Generated {Count} task(s) for failed dispatch of lead {LeadId} (tenant {TenantId}, reason {Reason})",
                rules.Count, evt.LeadId, evt.TenantId, evt.Reason);
            return;
        }

        var agencyId = lead.PreferredAgencyId ?? lead.AgencyId;
        var head = agencyId is { } a
            ? await administration.ResolveAgencyRecipientAsync(evt.TenantId, a, preferredUserId: null, ct)
            : null;

        if (head is not { } headId)
        {
            logger.LogInformation(
                "Lead {LeadId} is waiting for dispatch ({Reason}); no agency head to hand it to — it stays queued",
                evt.LeadId, evt.Reason);
            return;
        }

        db.CrmTasks.Add(CrmTask.Create(
            tenantId:         evt.TenantId,
            type:             CrmTaskType.ManualDispatch,
            priority:         CrmTaskPriority.High,
            title:            $"Dispatch manuel — {leadName}",
            dueAt:            now.AddHours(4),
            leadId:           evt.LeadId,
            assignedAgentId:  headId,
            description:      $"Aucun agent n’a pu recevoir ce lead ({ReasonLabel(evt.Reason)}). Il est en file "
                              + "d’attente et partira seul dès qu’un agent sera disponible ; vous pouvez aussi "
                              + "l’affecter vous-même. La tâche se ferme d’elle-même à l’affectation.",
            triggerEventType: TaskTriggerEvents.LeadDispatchingFailed,
            triggerEventId:   evt.LeadId));

        await db.SaveChangesAsync(ct);
        logger.LogInformation(
            "Lead {LeadId} is waiting for dispatch ({Reason}); manual-dispatch task given to agency head {HeadId}",
            evt.LeadId, evt.Reason, headId);
    }

    private static string ReasonLabel(string reason) => reason switch
    {
        "NO_AGENT_AVAILABLE"                  => "aucun agent disponible",
        "NO_AGENT_AVAILABLE_AFTER_EXCLUSIONS" => "tous les agents disponibles sont exclus par la règle",
        "NO_LICENSED_AGENT_AVAILABLE"         => "aucun agent titulaire de la carte professionnelle",
        "ALL_AGENTS_AT_TASK_CAPACITY"         => "tous les agents ont atteint leur plafond de tâches",
        "ANTI_MONOPOLY_BLOCKED"               => "seuil anti-monopole atteint par tous les agents",
        _                                     => reason,
    };

    private static string Render(string template, Guid leadId, string leadName) =>
        template.Replace("{LeadId}", leadId.ToString()).Replace("{LeadName}", leadName);
}
