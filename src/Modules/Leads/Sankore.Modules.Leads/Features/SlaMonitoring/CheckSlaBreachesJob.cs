namespace Sankore.Modules.Leads.Features.SlaMonitoring;

using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Sankore.Modules.Administration.PublicApi;
using Sankore.Modules.Leads.Domain;
using Sankore.Modules.Leads.Domain.Events;
using Sankore.Modules.Leads.Infrastructure;
using Sankore.Shared.Infrastructure.BackgroundJobs;

/// <summary>
/// Hangfire recurring job — runs per-tenant on a cron schedule.
/// 1. Detects assignments that have breached their SLA (first contact not recorded).
/// 2. Sends alert notifications via M08 (Notifications) (US-M13-141).
/// 3. Auto-escalates by reassigning to the agent's supervisor (US-M13-142).
///    Anti-loop protection: max escalation depth = 3.
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

    public async Task ExecuteAsync(Guid tenantId)
    {
        // SYSTEM identity — no HTTP context
        var systemUserId = Guid.Empty;
        using var bgCtx = BackgroundJobContext.SetScope(tenantId, systemUserId, "SYSTEM");
        using var scope = scopeFactory.CreateScope();

        var sp     = scope.ServiceProvider;
        var db     = sp.GetRequiredService<LeadsDbContext>();
        var admin  = sp.GetRequiredService<IAdministrationModule>();
        var clock  = sp.GetRequiredService<TimeProvider>();
        var logger = sp.GetRequiredService<ILogger<CheckSlaBreachesJob>>();

        var now = clock.GetUtcNow();

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

        if (breached.Count == 0) return;

        // Load corresponding leads (for status filter + domain events)
        var leadIds = breached.Select(a => a.LeadId).Distinct().ToList();
        var leads = await db.Leads
            .AsTracking()
            .IgnoreQueryFilters()
            .Where(l => l.TenantId == tenantId && leadIds.Contains(l.Id))
            .ToDictionaryAsync(l => l.Id);

        int alertsSent = 0, escalated = 0;

        foreach (var assignment in breached)
        {
            if (!leads.TryGetValue(assignment.LeadId, out var lead))
                continue;

            // Skip closed leads
            if (lead.Status is LeadStatus.Converted or LeadStatus.Archived
                             or LeadStatus.Lost or LeadStatus.Disqualified)
                continue;

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
                        TemplateKey: "lead.sla-breach",
                        IdempotencyKey: $"sla-breach-{assignment.Id}-{now:yyyyMMdd}",
                        TemplateData: new Dictionary<string, object>
                        {
                            ["lead_name"] = lead.FullName,
                            ["lead_id"] = lead.Id.ToString(),
                            ["sla_deadline"] =
                                assignment.SlaDeadline.ToString("g", CultureInfo.InvariantCulture),
                            ["breach_hours"] =
                                Math.Round((now - assignment.SlaDeadline).TotalHours, 1),
                        }),
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

            // ── US-M13-142: Auto-escalation ─────────────────────────────
            var currentDepth = ParseEscalationDepth(assignment.OverrideReason);

            if (currentDepth >= MaxEscalationDepth)
            {
                logger.LogWarning(
                    "Assignment {AssignmentId} has reached max escalation depth ({Depth}), skipping",
                    assignment.Id, currentDepth);
                continue;
            }

            // Find a supervisor to escalate to
            var teamIds = await admin.GetTeamAgentIdsAsync(tenantId, assignment.AgentId, CancellationToken.None);

            // If the current agent has no supervisors, skip escalation
            if (teamIds.Count == 0) continue;

            // Create new escalated assignment
            var newDepth = currentDepth + 1;
            var escalationReason = $"{EscalationPrefix}{newDepth} SLA breach auto-escalation from agent {assignment.AgentId}";

            // Extend SLA by the original window
            var originalWindow = assignment.SlaDeadline - assignment.CreatedAt;
            var newSlaDeadline = now.Add(originalWindow);

            var escalatedAssignment = LeadAssignment.CreateManualOverride(
                tenantId:    tenantId,
                leadId:      assignment.LeadId,
                agentId:     assignment.AgentId, // Keep same agent but record escalation
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

            // Raise auditable domain event
            lead.RaiseSlaEscalatedEvent(
                escalatedAssignment.Id, assignment.AgentId, newDepth);

            escalated++;
        }

        if (alertsSent > 0 || escalated > 0)
        {
            await db.SaveChangesAsync();

            logger.LogInformation(
                "SLA check for tenant {TenantId}: {Alerts} alert(s) sent, {Escalated} escalation(s)",
                tenantId, alertsSent, escalated);
        }
    }

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
