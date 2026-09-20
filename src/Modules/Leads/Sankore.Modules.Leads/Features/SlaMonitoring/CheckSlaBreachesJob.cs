namespace Sankore.Modules.Leads.Features.SlaMonitoring;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Sankore.Modules.Administration.PublicApi;
using Sankore.Modules.Leads.Domain;
using Sankore.Modules.Leads.Domain.Events;
using Sankore.Modules.Leads.Infrastructure;
using Sankore.Modules.Notifications.PublicApi;
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
        var notif  = sp.GetRequiredService<INotificationsModule>();
        var clock  = sp.GetRequiredService<TimeProvider>();
        var logger = sp.GetRequiredService<ILogger<CheckSlaBreachesJob>>();

        var now = clock.GetUtcNow();

        // ── Find breached assignments ───────────────────────────────────
        var breached = await db.LeadAssignments
            .AsTracking()
            .IgnoreQueryFilters()
            .Where(a => a.TenantId == tenantId
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
            var agent = await admin.GetAgentAsync(assignment.AgentId, CancellationToken.None);
            if (agent is not null)
            {
                try
                {
                    await notif.QueueEmailAsync(new QueueEmailRequest(
                        TemplateKey:    "lead.sla-breach",
                        RecipientEmail: $"{agent.FullName}@notification.internal",
                        RecipientName:  agent.FullName,
                        Module:         "Leads",
                        Locale:         "fr",
                        TemplateData: new Dictionary<string, object>
                        {
                            ["leadName"]    = lead.FullName,
                            ["leadId"]      = lead.Id.ToString(),
                            ["agentName"]   = agent.FullName,
                            ["slaDeadline"] = assignment.SlaDeadline.ToString("g"),
                            ["breachHours"] = Math.Round((now - assignment.SlaDeadline).TotalHours, 1),
                        },
                        IdempotencyKey: $"sla-breach-{assignment.Id}-{now:yyyyMMdd}",
                        TenantId:       tenantId));

                    alertsSent++;
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex,
                        "Failed to send SLA breach alert for assignment {AssignmentId}",
                        assignment.Id);
                }
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

            // Record first contact on the breached assignment to stop re-processing
            assignment.RecordFirstContact(now);

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
