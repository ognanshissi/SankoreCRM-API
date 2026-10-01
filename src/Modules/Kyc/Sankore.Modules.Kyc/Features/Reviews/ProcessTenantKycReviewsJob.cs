namespace Sankore.Modules.Kyc.Features.Reviews;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Sankore.Modules.Kyc.Domain;
using Sankore.Modules.Kyc.Infrastructure;
using Sankore.Modules.Kyc.Infrastructure.Settings;
using Sankore.Modules.Kyc.PublicApi;
using Sankore.Shared.Infrastructure.BackgroundJobs;
using Sankore.Shared.Infrastructure.Messaging;

/// <summary>
/// Hangfire job — the daily KYC review sweep of ONE tenant (KYC-B-07). Enqueued by
/// <see cref="KycReviewOrchestratorJob"/>; its only argument is an opaque tenant identifier.
///
/// <para>Three passes, in this order, and the order is load-bearing:</para>
/// <list type="number">
/// <item><b>Expired identity documents</b> — a full file whose document has expired drops to the
///   simplified tier. First, because it is a fact about the customer's papers and not a process:
///   a file that is also due for review must have its ceilings dropped before it enters the
///   review, not after it leaves.</item>
/// <item><b>Reviews that have come due</b> — the schedule is marked due, the file moves to
///   UnderReview and the agency is told.</item>
/// <item><b>Reviews past their grace period</b> — the file expires. Runs after pass 2 on purpose:
///   a review that comes due AND is already past grace in the same sweep (because the job did not
///   run for a fortnight) must be expired in that same sweep.</item>
/// </list>
///
/// <para>
/// Idempotent and resumable. Each pass selects on the state it is about to leave — Scheduled,
/// UnderReview, Full — so a second run on the same day finds nothing to do and publishes nothing.
/// Each file is processed in its own try/commit: one bad file out of four hundred is logged and
/// skipped, never a reason to abandon the other 399. Same shape as
/// <c>ProcessClientImportJob</c> in M01.
/// </para>
/// </summary>
public sealed class ProcessTenantKycReviewsJob(IServiceScopeFactory scopeFactory)
{
    public async Task ExecuteAsync(Guid tenantId)
    {
        // Set BEFORE the scope is created: ICurrentUser and ITenantContext are built from this
        // ambient context when the scope resolves them, and a scope opened first would capture the
        // HTTP implementations and find no request. The actor is SYSTEM — nobody decided that a
        // review came due, a date did.
        using var bgCtx = BackgroundJobContext.SetScope(tenantId, Guid.Empty, "SYSTEM");
        using var scope = scopeFactory.CreateScope();

        var sp = scope.ServiceProvider;
        var logger = sp.GetRequiredService<ILogger<ProcessTenantKycReviewsJob>>();

        var report = await RunAsync(
            sp.GetRequiredService<KycDbContext>(),
            sp.GetRequiredService<IKycSettings>(),
            sp.GetRequiredKeyedService<IEventPublisher>(nameof(KycDbContext)),
            sp.GetRequiredService<TimeProvider>(),
            logger,
            tenantId,
            CancellationToken.None);

        logger.LogInformation(
            "KYC review sweep for tenant {TenantId}: {Downgraded} downgraded, {MarkedDue} due, "
            + "{Expired} expired, {Failed} failed.",
            tenantId, report.Downgraded, report.MarkedDue, report.Expired, report.Failed);
    }

    /// <summary>
    /// The sweep itself, with its collaborators passed in rather than resolved.
    ///
    /// Separated from <see cref="ExecuteAsync"/> so the compliance rules above can be pinned by a
    /// test without standing up a DI container and a Hangfire storage — the scope handling is the
    /// only thing <see cref="ExecuteAsync"/> adds, and it is identical in every job of the repo.
    /// </summary>
    internal static async Task<KycReviewSweepReport> RunAsync(
        KycDbContext db,
        IKycSettings settings,
        IEventPublisher publisher,
        TimeProvider clock,
        ILogger logger,
        Guid tenantId,
        CancellationToken ct)
    {
        var today = DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime);
        var graceDays = await settings.GetIntAsync(tenantId, KycSettingKeys.ReviewGraceDays, ct);

        var report = new KycReviewSweepReport();

        await DowngradeFilesWithExpiredDocumentsAsync(
            db, publisher, clock, logger, tenantId, today, report, ct);

        await RaiseDueReviewsAsync(db, publisher, clock, logger, tenantId, today, report, ct);

        await ExpireFilesPastGraceAsync(db, clock, logger, tenantId, today, graceDays, report, ct);

        return report;
    }

    // ── Pass 1 — expired identity documents ─────────────────────────────────

    private static async Task DowngradeFilesWithExpiredDocumentsAsync(
        KycDbContext db, IEventPublisher publisher, TimeProvider clock, ILogger logger,
        Guid tenantId, DateOnly today, KycReviewSweepReport report, CancellationToken ct)
    {
        // The SQL predicate is only a pre-filter so the whole document table does not travel;
        // KycIdentityDocument.IsExpiredOn stays the authority on what "expired" means, and the
        // two must keep agreeing. IgnoreQueryFilters paired with an explicit tenant predicate:
        // a job runs outside any HTTP request, so the ambient tenant is not the one being swept.
        var candidates = await db.KycIdentityDocuments
            .IgnoreQueryFilters()
            .Where(d => d.TenantId == tenantId && d.ExpiryDate != null && d.ExpiryDate < today)
            .ToListAsync(ct);

        var fileIds = candidates
            .Where(d => d.IsExpiredOn(today))
            .Select(d => d.KycFileId)
            .Distinct()
            .ToList();

        foreach (var fileId in fileIds)
        {
            try
            {
                // Status Full, not tier Full: the transition table only allows the downgrade from
                // an approved full file, and selecting on it is also what makes the pass
                // idempotent — once downgraded the file is Simplified and no longer matches.
                var file = await db.KycFiles
                    .AsTracking()
                    .IgnoreQueryFilters()
                    .FirstOrDefaultAsync(
                        f => f.Id == fileId
                          && f.TenantId == tenantId
                          && f.Status == KycFileStatus.Full, ct);

                if (file is null) continue;

                var previousTier = file.Tier;

                var downgraded = file.DowngradeToSimplified(clock);
                if (downgraded.IsFailure)
                {
                    logger.LogWarning(
                        "KYC file {KycFileId} has an expired identity document but could not be "
                        + "downgraded: {Error}", fileId, downgraded.Error);
                    continue;
                }

                // Freezing deposits once the balance exceeds the simplified ceiling is M03's job,
                // and M03 does not exist yet. This event is the whole hand-off: M03 (savings) and
                // M07 (mobile money) consume it to drop their cached ceilings. Nothing here
                // touches an account balance.
                await publisher.PublishAsync(
                    new KycTierChangedEvent(
                        TenantId: tenantId,
                        CustomerEntityId: file.CustomerId,
                        PreviousTier: previousTier.ToString(),
                        CurrentTier: file.Tier.ToString(),
                        ChangedAt: clock.GetUtcNow()),
                    ct);

                await db.SaveChangesAsync(ct);
                report.Downgraded++;

                logger.LogInformation(
                    "KYC file {KycFileId} downgraded to Simplified: identity document expired.",
                    fileId);
            }
            catch (Exception ex)
            {
                report.Failed++;
                logger.LogError(
                    ex, "Downgrading KYC file {KycFileId} on an expired document failed.", fileId);

                // The status change and its outbox row commit together or not at all. Clearing
                // the tracker drops the half-applied mutation so the NEXT file is not saved with
                // this one's uncommitted change riding along.
                db.ChangeTracker.Clear();
            }
        }
    }

    // ── Pass 2 — reviews that have come due ─────────────────────────────────

    private static async Task RaiseDueReviewsAsync(
        KycDbContext db, IEventPublisher publisher, TimeProvider clock, ILogger logger,
        Guid tenantId, DateOnly today, KycReviewSweepReport report, CancellationToken ct)
    {
        // Ids only, then one fresh load per review: the loop clears the change tracker when a
        // file fails, which would detach every entity a single up-front materialisation held.
        var dueReviewIds = await db.KycReviewSchedules
            .IgnoreQueryFilters()
            .Where(r => r.TenantId == tenantId
                     && r.Status == KycReviewStatus.Scheduled
                     && r.DueDate <= today)
            .OrderBy(r => r.DueDate)
            .Select(r => r.Id)
            .ToListAsync(ct);

        foreach (var reviewId in dueReviewIds)
        {
            try
            {
                var review = await db.KycReviewSchedules
                    .AsTracking()
                    .IgnoreQueryFilters()
                    .FirstOrDefaultAsync(r => r.Id == reviewId && r.TenantId == tenantId, ct);

                // Status Scheduled is the idempotency key of this pass: a second sweep on the
                // same day finds the review Due and publishes nothing.
                if (review is null || review.Status != KycReviewStatus.Scheduled) continue;

                var file = await db.KycFiles
                    .AsTracking()
                    .IgnoreQueryFilters()
                    .FirstOrDefaultAsync(
                        f => f.Id == review.KycFileId && f.TenantId == tenantId, ct);

                if (file is null)
                {
                    logger.LogWarning(
                        "KYC review {ReviewId} points at file {KycFileId}, which no longer exists.",
                        reviewId, review.KycFileId);
                    continue;
                }

                var markedDue = review.MarkDue(clock);
                if (markedDue.IsFailure) continue;

                // A refused transition is logged, not fatal, and the review stays Due: the
                // deadline has passed whatever state the file is in, and the agency still has to
                // hear about it. A file already UnderReview from an earlier event-driven review is
                // the ordinary case here.
                var started = file.StartReview(clock);
                if (started.IsFailure)
                {
                    logger.LogWarning(
                        "KYC file {KycFileId} is due for review but is {Status}: {Error}",
                        file.Id, file.Status, started.Error);
                }

                await publisher.PublishAsync(
                    new KycReviewDueEvent(
                        TenantId: tenantId,
                        CustomerEntityId: file.CustomerId,
                        KycFileId: file.Id,
                        DueDate: review.DueDate,
                        Trigger: review.Trigger.ToString(),
                        RaisedAt: clock.GetUtcNow()),
                    ct);

                await db.SaveChangesAsync(ct);
                report.MarkedDue++;
            }
            catch (Exception ex)
            {
                report.Failed++;
                logger.LogError(ex, "Raising KYC review {ReviewId} failed.", reviewId);
                db.ChangeTracker.Clear();
            }
        }
    }

    // ── Pass 3 — reviews past their grace period ────────────────────────────

    private static async Task ExpireFilesPastGraceAsync(
        KycDbContext db, TimeProvider clock, ILogger logger,
        Guid tenantId, DateOnly today, int graceDays, KycReviewSweepReport report,
        CancellationToken ct)
    {
        // Pre-filter on the cutoff rather than on `today`: the grace period runs from the DUE
        // DATE, so a sweep that did not run for a fortnight must not hand out a free fortnight.
        // KycReviewSchedule.IsPastGrace below is what actually decides.
        var graceCutoff = today.AddDays(-graceDays);

        var overdueReviewIds = await db.KycReviewSchedules
            .IgnoreQueryFilters()
            .Where(r => r.TenantId == tenantId
                     && r.Status == KycReviewStatus.Due
                     && r.DueDate < graceCutoff)
            .Select(r => r.Id)
            .ToListAsync(ct);

        foreach (var reviewId in overdueReviewIds)
        {
            try
            {
                var review = await db.KycReviewSchedules
                    .AsNoTracking()
                    .IgnoreQueryFilters()
                    .FirstOrDefaultAsync(r => r.Id == reviewId && r.TenantId == tenantId, ct);

                if (review is null || !review.IsPastGrace(today, graceDays)) continue;

                // Only a file actually under review expires. Selecting on the status is also the
                // idempotency key: once expired the file no longer matches, and the review row
                // deliberately STAYS Due — the review is still owed, it is merely late.
                var file = await db.KycFiles
                    .AsTracking()
                    .IgnoreQueryFilters()
                    .FirstOrDefaultAsync(
                        f => f.Id == review.KycFileId
                          && f.TenantId == tenantId
                          && f.Status == KycFileStatus.UnderReview, ct);

                if (file is null) continue;

                var expired = file.Expire(clock);
                if (expired.IsFailure)
                {
                    logger.LogWarning(
                        "KYC file {KycFileId} is past its review grace period but could not be "
                        + "expired: {Error}", file.Id, expired.Error);
                    continue;
                }

                // Expiry is NOT a rejection. The customer keeps a valid but capped relationship —
                // KycFile.IsOpen stays true, the file is still theirs, and completing the review
                // brings it back (Expired → UnderReview is the only way out). Nothing here closes
                // an account or publishes a rejection.
                await db.SaveChangesAsync(ct);
                report.Expired++;

                logger.LogInformation(
                    "KYC file {KycFileId} expired: review due {DueDate} not done within "
                    + "{GraceDays} day(s).", file.Id, review.DueDate, graceDays);
            }
            catch (Exception ex)
            {
                report.Failed++;
                logger.LogError(
                    ex, "Expiring the file of KYC review {ReviewId} failed.", reviewId);
                db.ChangeTracker.Clear();
            }
        }
    }
}

/// <summary>What one sweep did, for the log line and for the tests.</summary>
internal sealed class KycReviewSweepReport
{
    public int Downgraded { get; set; }
    public int MarkedDue { get; set; }
    public int Expired { get; set; }
    public int Failed { get; set; }
}
