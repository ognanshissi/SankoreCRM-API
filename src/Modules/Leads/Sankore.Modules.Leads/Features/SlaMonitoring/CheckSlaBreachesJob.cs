namespace Sankore.Modules.Leads.Features.SlaMonitoring;

using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Sankore.Modules.Administration.PublicApi;
using Sankore.Modules.Leads.Domain;
using Sankore.Modules.Leads.Domain.Events;
using Sankore.Modules.Leads.Features.Tasks;
using Sankore.Modules.Leads.Infrastructure;
using Sankore.Shared.Infrastructure.BackgroundJobs;

/// <summary>
/// Hangfire recurring job — runs per-tenant every 15 minutes.
/// 1. Warns the agent once when an assignment's first-contact SLA is about to be breached
///    (<see cref="LeadSettingKeys.SlaWarningThresholdPercent"/> of the window elapsed).
/// 2. Detects assignments that have breached their SLA (first contact not recorded) and alerts
///    the agent, with a copy to the head of the lead's agency (US-M13-141).
/// 3. Escalates (US-M13-142): the agent KEEPS the lead and its tasks — the manager is told, not
///    substituted — and the deadline is extended by the original window, with the overdue tasks
///    following it and the extension written into each task's attribution history.
///    Anti-loop protection: max escalation depth = 3; past it only the daily alerts remain.
/// Runs under SYSTEM identity.
/// </summary>
public sealed class CheckSlaBreachesJob(IServiceScopeFactory scopeFactory)
{
    /// <summary>Maximum escalation depth to prevent infinite loops.</summary>
    private const int MaxEscalationDepth = 3;

    /// <summary>
    /// Metadata column that tracks how many times an assignment has been escalated.
    /// Stored in OverrideReason as "ESCALATED:N" prefix.
    /// </summary>
    private const string EscalationPrefix = "ESCALATED:";

    internal const string WarningTemplateKey = "lead.sla-warning";
    internal const string BreachTemplateKey = "lead.sla-breach";
    internal const string ManagerBreachTemplateKey = "lead.sla-breach-manager";

    public async Task ExecuteAsync(Guid tenantId)
    {
        // SYSTEM identity — no HTTP context
        var systemUserId = Guid.Empty;
        using var bgCtx = BackgroundJobContext.SetScope(tenantId, systemUserId, "SYSTEM");
        using var scope = scopeFactory.CreateScope();

        var sp       = scope.ServiceProvider;
        var db       = sp.GetRequiredService<LeadsDbContext>();
        var admin    = sp.GetRequiredService<IAdministrationModule>();
        var settings = sp.GetRequiredService<ILeadSettings>();
        var clock    = sp.GetRequiredService<TimeProvider>();
        var logger   = sp.GetRequiredService<ILogger<CheckSlaBreachesJob>>();

        var now = clock.GetUtcNow();

        var warningPercent = await settings.GetIntAsync(
            tenantId, LeadSettingKeys.SlaWarningThresholdPercent, CancellationToken.None);

        var warned = await WarnApproachingAsync(db, admin, logger, tenantId, warningPercent, now);

        // ── Find breached assignments ───────────────────────────────────
        var breached = await db.LeadAssignments
            .AsTracking()
            .IgnoreQueryFilters()
            .Where(a => a.TenantId == tenantId
                     // Open assignments only. A superseded row can never be closed —
                     // RecordFirstContactHandler only ever stamps Lead.CurrentAssignmentId — so
                     // before this predicate every reassignment left behind a row that mailed its
                     // former agent an SLA alert every single day the lead stayed open.
                     && a.SupersededAt == null
                     && a.FirstContactAt == null
                     && a.SlaDeadline < now)
            .ToListAsync();

        int alertsSent = 0, managerAlerts = 0, escalated = 0, tasksExtended = 0;

        if (breached.Count > 0)
        {
            // Load corresponding leads (for status filter + domain events)
            var leadIds = breached.Select(a => a.LeadId).Distinct().ToList();
            var leads = await db.Leads
                .AsTracking()
                .IgnoreQueryFilters()
                .Where(l => l.TenantId == tenantId && leadIds.Contains(l.Id))
                .ToDictionaryAsync(l => l.Id);

            foreach (var assignment in breached)
            {
                if (!leads.TryGetValue(assignment.LeadId, out var lead) || IsClosed(lead))
                    continue;

                var breachData = new Dictionary<string, object>
                {
                    ["lead_name"] = lead.FullName,
                    ["lead_id"] = lead.Id.ToString(),
                    ["sla_deadline"] =
                        assignment.SlaDeadline.ToString("g", CultureInfo.InvariantCulture),
                    ["breach_hours"] =
                        Math.Round((now - assignment.SlaDeadline).TotalHours, 1),
                };

                // ── US-M13-141: Send alert via M08 ──────────────────────────
                // Addressed through M12, which owns the agent's address and locale. It used to queue
                // in M08 directly against "{agent.FullName}@notification.internal" — a synthetic
                // address, with spaces in its local part — and with camelCase template data against a
                // snake_case template, so the alert reached nobody and would have rendered empty if
                // it had. Both halves are fixed here; NotifyUserAsync never throws, which is why the
                // try/catch and the existence check are gone.
                if (await admin.NotifyUserAsync(
                        new NotifyUserRequest(
                            TenantId: tenantId,
                            UserId: assignment.AgentId,
                            TemplateKey: BreachTemplateKey,
                            IdempotencyKey: $"sla-breach-{assignment.Id}-{now:yyyyMMdd}",
                            TemplateData: breachData),
                        CancellationToken.None))
                {
                    alertsSent++;
                }
                else
                {
                    logger.LogWarning(
                        "No SLA breach alert sent for assignment {AssignmentId}: agent {AgentId} is "
                        + "unknown or has no email address",
                        assignment.Id, assignment.AgentId);
                }

                // ── Copy to the head of the agency ──────────────────────────
                // The escalation used to look for "a supervisor" through GetTeamAgentIdsAsync, which
                // answers the opposite question — the agents a given user SUPERVISES — and was asked
                // it about the agent, so an ordinary agent never escalated at all. The manager is
                // now resolved off the agency, like the remote-KYC chase does, and is COPIED: the
                // agent keeps the lead, the manager knows it is late.
                if (await NotifyManagerAsync(admin, tenantId, lead, assignment, breachData, now))
                    managerAlerts++;

                // ── US-M13-142: Auto-escalation ─────────────────────────────
                var currentDepth = ParseEscalationDepth(assignment.OverrideReason);

                if (currentDepth >= MaxEscalationDepth)
                {
                    logger.LogWarning(
                        "Assignment {AssignmentId} has reached max escalation depth ({Depth}), skipping",
                        assignment.Id, currentDepth);
                    continue;
                }

                var newDepth = currentDepth + 1;
                var escalationReason = $"{EscalationPrefix}{newDepth} SLA breach auto-escalation from agent {assignment.AgentId}";

                // Extend SLA by the original window
                var originalWindow = assignment.SlaDeadline - assignment.CreatedAt;
                var newSlaDeadline = now.Add(originalWindow);

                var escalatedAssignment = LeadAssignment.CreateManualOverride(
                    tenantId:    tenantId,
                    leadId:      assignment.LeadId,
                    agentId:     assignment.AgentId, // the agent keeps the lead; the manager is copied
                    reason:      escalationReason,
                    slaDeadline: newSlaDeadline,
                    createdAt:   now);

                // The escalated row becomes the lead's current assignment, which supersedes the breached
                // one. It used to be "closed" with RecordFirstContact(now) purely as a stop-reprocessing
                // flag — i.e. by recording a call that never happened, so the row then counted as a late
                // CONTACT in GetAgentPerformance. And the escalated row, never being made the lead's
                // current one, could never be closed at all: RecordFirstContactHandler only ever stamps
                // Lead.CurrentAssignmentId, so once it breached in turn it would have alerted for ever.
                var escalationResult = lead.AssignTo(escalatedAssignment, assignment);

                if (escalationResult.IsFailure)
                {
                    logger.LogWarning(
                        "Assignment {AssignmentId}: escalation refused ({Error})",
                        assignment.Id, escalationResult.Error);
                    continue;
                }

                db.LeadAssignments.Add(escalatedAssignment);

                // The task is KEPT: its deadline follows the escalated assignment, and the change is
                // written into its attribution history. Without this the first-contact task stayed
                // on the breached deadline, overdue for ever on the agent's list while the SLA said
                // otherwise.
                tasksExtended += await AssignmentTasks.ExtendOnEscalationAsync(
                    db, tenantId, lead.Id, assignment.AgentId, newSlaDeadline, newDepth, now,
                    CancellationToken.None);

                // Raise auditable domain event
                lead.RaiseSlaEscalatedEvent(
                    escalatedAssignment.Id, assignment.AgentId, newDepth);

                escalated++;
            }
        }

        if (db.ChangeTracker.HasChanges())
            await db.SaveChangesAsync();

        if (warned > 0 || alertsSent > 0 || managerAlerts > 0 || escalated > 0)
        {
            logger.LogInformation(
                "SLA check for tenant {TenantId}: {Warned} warning(s), {Alerts} alert(s), "
                + "{ManagerAlerts} manager copy(ies), {Escalated} escalation(s), {Tasks} task(s) extended",
                tenantId, warned, alertsSent, managerAlerts, escalated, tasksExtended);
        }
    }

    /// <summary>
    /// The pre-breach warning: once per assignment, to the agent only — it is still theirs to save.
    /// Marked on the row whether or not a message could be sent, so an agent without an address
    /// is not re-resolved every 15 minutes until the deadline.
    /// </summary>
    private static async Task<int> WarnApproachingAsync(
        LeadsDbContext db,
        IAdministrationModule admin,
        ILogger logger,
        Guid tenantId,
        int warningPercent,
        DateTimeOffset now)
    {
        if (warningPercent is < 1 or > 99) return 0;

        // The percentage is applied in memory: the open, not-yet-warned assignments of one tenant
        // are a handful of rows (the filtered index covers them), and the arithmetic on two
        // timestamps does not translate portably.
        var candidates = await db.LeadAssignments
            .AsTracking()
            .IgnoreQueryFilters()
            .Where(a => a.TenantId == tenantId
                     && a.SupersededAt == null
                     && a.FirstContactAt == null
                     && a.SlaWarnedAt == null
                     && a.SlaDeadline >= now)
            .ToListAsync();

        var due = candidates.Where(a => a.ShouldWarnSla(now, warningPercent)).ToList();
        if (due.Count == 0) return 0;

        var leadIds = due.Select(a => a.LeadId).Distinct().ToList();
        var leads = await db.Leads
            .IgnoreQueryFilters()
            .Where(l => l.TenantId == tenantId && leadIds.Contains(l.Id))
            .ToDictionaryAsync(l => l.Id);

        var warned = 0;
        foreach (var assignment in due)
        {
            if (!leads.TryGetValue(assignment.LeadId, out var lead) || IsClosed(lead))
                continue;

            var sent = await admin.NotifyUserAsync(
                new NotifyUserRequest(
                    TenantId: tenantId,
                    UserId: assignment.AgentId,
                    TemplateKey: WarningTemplateKey,
                    IdempotencyKey: $"sla-warning-{assignment.Id}",
                    TemplateData: new Dictionary<string, object>
                    {
                        ["lead_name"] = lead.FullName,
                        ["lead_id"] = lead.Id.ToString(),
                        ["sla_deadline"] =
                            assignment.SlaDeadline.ToString("g", CultureInfo.InvariantCulture),
                        ["remaining_hours"] =
                            Math.Round((assignment.SlaDeadline - now).TotalHours, 1),
                    }),
                CancellationToken.None);

            if (!sent)
            {
                logger.LogWarning(
                    "No SLA warning sent for assignment {AssignmentId}: agent {AgentId} is unknown "
                    + "or has no email address",
                    assignment.Id, assignment.AgentId);
            }

            assignment.MarkSlaWarned(now);
            warned++;
        }

        return warned;
    }

    /// <summary>
    /// Copies the breach to the head of the lead's agency — or of the agent's own when the lead
    /// carries none. Nobody to copy (no agency, no active manager, or the agent IS the manager) is
    /// an ordinary outcome: the agent's own alert still went out.
    /// </summary>
    private static async Task<bool> NotifyManagerAsync(
        IAdministrationModule admin,
        Guid tenantId,
        Lead lead,
        LeadAssignment assignment,
        IReadOnlyDictionary<string, object> breachData,
        DateTimeOffset now)
    {
        var agent = await admin.GetAgentAsync(assignment.AgentId, CancellationToken.None);

        var agencyId = lead.AgencyId ?? agent?.AgencyId;
        if (agencyId is not { } id || id == Guid.Empty) return false;

        var managerId = await admin.ResolveAgencyRecipientAsync(
            tenantId, id, preferredUserId: null, CancellationToken.None);
        if (managerId is not { } manager || manager == assignment.AgentId) return false;

        var data = new Dictionary<string, object>(breachData)
        {
            ["agent_name"] = agent?.FullName ?? string.Empty,
        };

        // Its own key: M08 deduplicates per message, so sharing the agent's would silence the copy.
        return await admin.NotifyUserAsync(
            new NotifyUserRequest(
                TenantId: tenantId,
                UserId: manager,
                TemplateKey: ManagerBreachTemplateKey,
                IdempotencyKey: $"sla-breach-manager-{assignment.Id}-{manager}-{now:yyyyMMdd}",
                TemplateData: data),
            CancellationToken.None);
    }

    private static bool IsClosed(Lead lead)
        => lead.Status is LeadStatus.Converted or LeadStatus.Archived
                       or LeadStatus.Lost or LeadStatus.Disqualified;

    private static int ParseEscalationDepth(string? overrideReason)
    {
        if (overrideReason is null || !overrideReason.StartsWith(EscalationPrefix))
            return 0;

        var depthStr = overrideReason[EscalationPrefix.Length..];
        var spaceIdx = depthStr.IndexOf(' ');
        if (spaceIdx > 0) depthStr = depthStr[..spaceIdx];

        return int.TryParse(depthStr, out var depth) ? depth : 0;
    }
}
