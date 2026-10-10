namespace Sankore.Modules.Leads.Features.Questionnaires;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Sankore.Modules.Leads.Domain;
using Sankore.Modules.Leads.Infrastructure;
using Sankore.Shared.Infrastructure.BackgroundJobs;
using Sankore.Shared.Kernel;

/// <summary>
/// Blanks health questionnaire answers once the tenant's retention period has elapsed since they
/// were handed to the insurance back-office (BNF-B-09).
///
/// <para>
/// <b>The clock starts at TRANSMISSION, not at creation.</b> Until the answers reach the insurer
/// they are the only copy, and purging them would lose the subscription they justify. A response
/// with no <c>TransmittedAt</c> is therefore never purged — however old it is — which also means a
/// transmission that never happens leaves data behind. That is the right failure: the alternative
/// is destroying a declaration nobody received.
/// </para>
///
/// <para>
/// The ROW survives: it says a declaration was made, by whom and when. Only the content goes,
/// which is what a retention period limits.
/// </para>
/// </summary>
// Public, not internal: Program.cs registers the recurring job by type, exactly as it does
// for every other job in this module.
public sealed class PurgeHealthResponsesJob(
    IServiceScopeFactory scopeFactory,
    ILogger<PurgeHealthResponsesJob> logger)
{
    /// <summary>Rows per transaction. Small enough that an interrupted run loses little work.</summary>
    private const int BatchSize = 500;

    private const int MaxBatchesPerRun = 200;

    /// <summary>
    /// Runs the purge for one tenant.
    /// </summary>
    /// <remarks>
    /// <c>BackgroundJobContext.SetScope</c> is established BEFORE the DI scope is created, because
    /// <c>ITenantContext</c> and <c>ICurrentUser</c> are built from it and a Hangfire job has no
    /// HTTP context. Getting that order wrong is the documented way this module's jobs fail.
    /// </remarks>
    public async Task RunAsync(Guid tenantId, CancellationToken ct = default)
    {
        BackgroundJobContext.SetScope(tenantId, Guid.Empty, "SYSTEM");

        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LeadsDbContext>();
        var settings = scope.ServiceProvider.GetRequiredService<ILeadSettings>();
        var clock = scope.ServiceProvider.GetRequiredService<TimeProvider>();

        var retentionDays = await settings.GetIntAsync(
            tenantId, LeadSettingKeys.HealthResponseRetentionDays, ct);

        // Zero would purge at the moment of transmission. Treated as "not configured" rather than
        // honoured: a tenant that genuinely wants immediate deletion is asking for something the
        // insurer's own acknowledgement window makes unsafe, and a mistyped 0 must not shred a
        // file the same day it was sent.
        if (retentionDays <= 0)
        {
            logger.LogWarning(
                "Health response retention for tenant {TenantId} is {Days} days; nothing purged.",
                tenantId, retentionDays);
            return;
        }

        var cutoff = clock.GetUtcNow().AddDays(-retentionDays);
        var purged = 0;

        for (var batch = 0; batch < MaxBatchesPerRun; batch++)
        {
            // IgnoreQueryFilters + an EXPLICIT tenant predicate: the job's ambient tenant comes
            // from BackgroundJobContext and the pairing is the documented rule for this module —
            // the filter alone would be trusting an AsyncLocal to have been set.
            var due = await db.QualificationResponses
                .IgnoreQueryFilters()
                .AsTracking()
                .Where(r => r.TenantId == tenantId
                         && r.IsHealthData
                         && !r.IsPurged
                         && r.TransmittedAt != null
                         && r.TransmittedAt < cutoff)
                .Take(BatchSize)
                .ToListAsync(ct);

            if (due.Count == 0)
                break;

            foreach (var response in due)
                response.Purge();

            await db.SaveChangesAsync(ct);
            db.ChangeTracker.Clear();
            purged += due.Count;
        }

        if (purged > 0)
        {
            // The COUNT only. Logging which lead or which subject would put the existence of a
            // named person's health declaration into the log aggregator, which is the one place
            // the permission gate does not reach.
            logger.LogInformation(
                "Purged {Count} health questionnaire response(s) for tenant {TenantId} "
                + "transmitted before {Cutoff}.",
                purged, tenantId, cutoff);
        }
    }
}
