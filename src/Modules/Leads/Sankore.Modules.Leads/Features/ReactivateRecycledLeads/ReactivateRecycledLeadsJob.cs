namespace Sankore.Modules.Leads.Features.ReactivateRecycledLeads;

using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Sankore.Modules.Leads.Domain;
using Sankore.Modules.Leads.Features.RecalculateLeadScore;
using Sankore.Modules.Leads.Infrastructure;
using Sankore.Shared.Infrastructure.BackgroundJobs;

/// <summary>
/// Hangfire recurring job — runs per-tenant (US-M13-160/161).
/// Scans Recycled leads that have waited beyond the configurable reactivation
/// threshold, re-verifies consent if dormant too long, reactivates them, and
/// re-triggers the scoring pipeline. Runs under SYSTEM identity.
/// Reactivation queue is strictly tenant-scoped (IgnoreQueryFilters + manual tenant filter).
/// </summary>
public sealed class ReactivateRecycledLeadsJob(IServiceScopeFactory scopeFactory)
{
    public async Task ExecuteAsync(Guid tenantId)
    {
        var systemUserId = Guid.Empty;
        using var bgCtx = BackgroundJobContext.SetScope(tenantId, systemUserId, "SYSTEM");
        using var scope = scopeFactory.CreateScope();

        var sp       = scope.ServiceProvider;
        var db       = sp.GetRequiredService<LeadsDbContext>();
        var sender   = sp.GetRequiredService<ISender>();
        var clock    = sp.GetRequiredService<TimeProvider>();
        var settings = sp.GetRequiredService<IOptions<LeadModuleSettings>>().Value;
        var logger   = sp.GetRequiredService<ILogger<ReactivateRecycledLeadsJob>>();

        var now = clock.GetUtcNow();
        var reactivationCutoff = now.AddDays(-settings.RecycledLeadReactivationDays);
        var dormancyCutoff     = now.AddDays(-settings.ConsentReverificationDormancyDays);

        // ── Find recycled leads past the reactivation threshold ─────────
        var candidates = await db.Leads
            .AsTracking()
            .IgnoreQueryFilters()
            .Where(l => l.TenantId == tenantId
                     && l.Status == LeadStatus.Recycled
                     && l.UpdatedAt <= reactivationCutoff)
            .ToListAsync();

        if (candidates.Count == 0) return;

        // Pre-load active DataProcessing consents for dormancy check
        var leadIds = candidates.Select(l => l.Id).ToList();
        var activeConsents = await db.LeadConsents
            .IgnoreQueryFilters()
            .Where(c => c.TenantId == tenantId
                     && leadIds.Contains(c.LeadId)
                     && c.Type == ConsentType.DataProcessing
                     && c.Status == ConsentStatus.Active)
            .Select(c => c.LeadId)
            .ToHashSetAsync();

        int reactivated = 0, skippedConsent = 0;

        foreach (var lead in candidates)
        {
            // ── US-M13-161: Re-verify consent if dormant beyond threshold ──
            var lastActivity = lead.LastActivityAt ?? lead.UpdatedAt;
            var isDormant = lastActivity <= dormancyCutoff;

            if (isDormant && !activeConsents.Contains(lead.Id))
            {
                // Consent expired or withdrawn — cannot reactivate without re-consent
                logger.LogInformation(
                    "Skipping reactivation of lead {LeadId}: dormant and no active DataProcessing consent",
                    lead.Id);
                skippedConsent++;
                continue;
            }

            // ── Reactivate ──────────────────────────────────────────────
            var result = lead.Reactivate();
            if (result.IsFailure) continue;

            reactivated++;
        }

        if (reactivated > 0)
            await db.SaveChangesAsync();

        // ── Re-trigger scoring pipeline for reactivated leads (US-M13-161 → US-M13-050) ──
        foreach (var lead in candidates.Where(l => l.Status == LeadStatus.Open))
        {
            try
            {
                await sender.Send(
                    new RecalculateLeadScoreCommand(lead.Id, "LEAD_REACTIVATED"),
                    CancellationToken.None);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex,
                    "Failed to recalculate score for reactivated lead {LeadId}", lead.Id);
            }
        }

        logger.LogInformation(
            "Reactivation job for tenant {TenantId}: {Reactivated} reactivated, {Skipped} skipped (consent required)",
            tenantId, reactivated, skippedConsent);
    }
}
