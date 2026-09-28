namespace Sankore.Modules.Customers.Features.Compliance.Retention;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Sankore.Modules.Customers.Domain;
using Sankore.Modules.Customers.Features.Timeline.Projection;
using Sankore.Modules.Customers.Infrastructure;
using Sankore.Modules.Kyc.PublicApi;
using Sankore.Shared.Infrastructure.BackgroundJobs;

/// <summary>
/// Monthly per-tenant job (US-M01-BE-29). Lists the clients whose archive has outlived the
/// tenant's <c>retention-years</c> and proposes them for anonymization.
/// <para>
/// The proposal is a timeline entry (<c>SourceModule = "Customers"</c>,
/// <c>EntryType = "RETENTION_ELIGIBLE"</c>) and not a new table: the entry is already
/// tenant-scoped, deduplicated and visible on the client file, and a retention queue would be a
/// second source of truth to keep in sync with the client status.
/// </para>
/// <para>
/// The job proposes, it never erases. Anonymization stays an explicit human act through
/// <c>POST clients/{clientId}/anonymize</c>, because it is irreversible.
/// </para>
/// </summary>
public sealed class IdentifyRetentionCandidatesJob(IServiceScopeFactory scopeFactory)
{
    /// <summary>Hangfire entry point. The tenant id is the only argument — an opaque identifier.</summary>
    public async Task ExecuteAsync(Guid tenantId)
    {
        // Guid.Empty as the actor: the whole run is attributed to the SYSTEM account, which is
        // also what the audit trail and the timeline entries will show.
        using var bgCtx = BackgroundJobContext.SetScope(tenantId, Guid.Empty, "SYSTEM");
        using var scope = scopeFactory.CreateScope();

        var sp = scope.ServiceProvider;
        var db = sp.GetRequiredService<CustomersDbContext>();
        var settings = sp.GetRequiredService<ICustomerSettings>();
        var kyc = sp.GetRequiredService<IKycModule>();
        var projector = sp.GetRequiredService<IClientTimelineProjector>();
        var clock = sp.GetRequiredService<TimeProvider>();
        var logger = sp.GetRequiredService<ILogger<IdentifyRetentionCandidatesJob>>();

        var now = clock.GetUtcNow();
        var retentionYears = await RetentionWindow.ResolveAsync(settings, tenantId, CancellationToken.None);
        var cutOff = RetentionWindow.CutOff(now, retentionYears);

        // IgnoreQueryFilters + an explicit TenantId predicate: outside the HTTP pipeline the
        // ambient tenant comes from BackgroundJobContext, and relying on it implicitly here is
        // exactly how a cross-tenant leak gets introduced.
        var candidates = await db.Clients
            .IgnoreQueryFilters()
            .Where(c => c.TenantId == tenantId
                        && c.Status == ClientStatus.Archived
                        && !c.IsAnonymized
                        && c.ArchivedAt != null
                        && c.ArchivedAt < cutOff)
            .Select(c => new { c.Id, c.ClientNumber, c.ArchivedAt })
            .ToListAsync();

        int proposed = 0, heldByKyc = 0;

        foreach (var candidate in candidates)
        {
            // A KYC file still under obligation (dispute, investigation, regulatory hold) keeps
            // the client out of the proposal entirely — it is not listed and then filtered by a
            // human, it is never proposed.
            var cleared = await kyc.IsRetentionClearedAsync(tenantId, candidate.Id, CancellationToken.None);
            if (!cleared)
            {
                heldByKyc++;
                continue;
            }

            // Summary carries no personal data: a client number and a date, never a name, a
            // phone number, a document number or an address.
            var summary =
                $"Retention period of {retentionYears} years elapsed for archived client " +
                $"{candidate.ClientNumber} (archived {candidate.ArchivedAt:yyyy-MM-dd}); " +
                "anonymization is now allowed.";

            await projector.AppendAsync(
                tenantId: tenantId,
                clientId: candidate.Id,
                sourceModule: RetentionWindow.TimelineSourceModule,
                entryType: RetentionWindow.RetentionEligibleEntryType,
                occurredAt: now,
                summary: summary,
                referenceType: "Client",
                referenceId: candidate.Id.ToString("D"),
                dedupKey: RetentionWindow.EligibilityDedupKey(candidate.Id, now),
                ct: CancellationToken.None);

            proposed++;
        }

        logger.LogInformation(
            "Retention scan for tenant {TenantId}: {Proposed} client(s) proposed for anonymization, " +
            "{HeldByKyc} held by KYC, out of {Scanned} archived beyond {RetentionYears} years.",
            tenantId, proposed, heldByKyc, candidates.Count, retentionYears);
    }
}
