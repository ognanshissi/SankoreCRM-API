namespace Sankore.Modules.Kyc;

using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Logging;
using Sankore.Modules.Kyc.Infrastructure.Settings;
using Sankore.Modules.Kyc.Domain;
using Sankore.Modules.Kyc.Infrastructure;
using Sankore.Modules.Kyc.PublicApi;
using Sankore.Shared.Kernel.Authorization;

/// <summary>
/// Implementation of <see cref="IKycModule"/>. Callers in other modules see only the interface.
///
/// It replaces <c>StubKycModule</c>, and that swap is not neutral: the stub answered
/// <c>NotStarted</c> to every status question and <c>false</c> to every retention question. M01's
/// retention job and its anonymisation handler already call both. Registering this facade
/// therefore changes what those two see on the day it ships — deliberately, and only for
/// <see cref="GetStatusAsync"/>.
/// </summary>
public sealed class KycModuleFacade(
    KycDbContext db,
    IKycSettings settings,
    IDistributedCache cache,
    ILogger<KycModuleFacade> logger,
    IAgencyScopeProvider? agencyScope = null,
    Features.Files.KycVisibility? visibility = null,
    Sankore.Shared.Infrastructure.Auth.ICurrentUser? currentUser = null) : IKycModule
{
    public Task<KycFileSummary?> GetKycFileSummaryAsync(Guid tenantId, Guid kycFileId, CancellationToken ct) =>
        db.KycFiles
            .IgnoreQueryFilters()
            .Where(f => f.TenantId == tenantId && f.Id == kycFileId)
            .Select(f => new KycFileSummary(f.Id, f.CustomerId, f.AgencyId, f.Status.ToString()))
            .FirstOrDefaultAsync(ct);

    public async Task<bool> CanActorAccessKycFileAsync(Guid tenantId, Guid kycFileId, Guid actorUserId, CancellationToken ct)
    {
        var file = await db.KycFiles
            .IgnoreQueryFilters()
            .Where(f => f.TenantId == tenantId && f.Id == kycFileId)
            .Select(f => new { f.AgencyId, f.ResponsibleAgentId, f.CreatedBy, f.LastSubmittedBy })
            .FirstOrDefaultAsync(ct);

        if (file is null) return false;

        // Assigned-only reads, when the actor IS the signed-in caller — the only one whose
        // permissions are known here, and the only actor any caller passes today.
        if (visibility is { SeesAll: false } && currentUser?.Id == actorUserId
            && !(file.ResponsibleAgentId == actorUserId
                 || (file.ResponsibleAgentId == null
                     && (file.CreatedBy == actorUserId || file.LastSubmittedBy == actorUserId))))
            return false;

        // Same rule as KycFileAccess, for an actor named by the caller instead of the HTTP user.
        // No scope provider registered = no way to check = refused.
        if (agencyScope is null) return false;

        var perimeter = await agencyScope.GetAccessibleAgencyIdsAsync(tenantId, actorUserId, ct);
        return perimeter is null || (file.AgencyId is { } agency && perimeter.Contains(agency));
    }

    public async Task<KycStatus?> GetStatusAsync(
        Guid tenantId, Guid customerEntityId, CancellationToken ct)
    {
        // IgnoreQueryFilters plus an explicit tenant predicate: this runs for callers outside any
        // HTTP context (a Hangfire job of M01), so the ambient tenant cannot be trusted to be the
        // one being asked about.
        var status = await db.KycFiles
            .IgnoreQueryFilters()
            .Where(f => f.TenantId == tenantId && f.CustomerId == customerEntityId)
            // A customer may carry closed files alongside the open one. The open file is the
            // answer; among closed ones the most recent is.
            .OrderByDescending(f => f.Status != KycFileStatus.Rejected && f.Status != KycFileStatus.Suspended)
            .ThenByDescending(f => f.UpdatedAt)
            .Select(f => (KycFileStatus?)f.Status)
            .FirstOrDefaultAsync(ct);

        // Null, not NotStarted: the contract distinguishes "no file" from a file in a state. M01
        // treats a null the way it treated the stub's NotStarted.
        return status?.ToPublicStatus();
    }

    /// <summary>
    /// Whether this module still needs the client's KYC evidence (data-purge lot 2, 2026-10-24).
    ///
    /// <para>
    /// <b>How long</b> evidence is kept after the end of the relationship is the client's retention
    /// (<c>retention-years</c>, LBC/FT, floored at 10), checked by M01 before it asks here — one
    /// duration, not two that could disagree. What this module adds is what only it knows: a file
    /// still being worked (collection, verification, validation, complement, review) or
    /// <b>suspended</b> — a compliance block, which is this module's legal hold. Any of those, and
    /// the answer is no.
    /// </para>
    ///
    /// <para>
    /// Before, this answered false unconditionally ("the rule is not implemented yet"), so no
    /// client could ever be anonymised, by the monthly proposal or by hand.
    /// </para>
    /// </summary>
    public Task<bool> IsRetentionClearedAsync(Guid tenantId, Guid customerEntityId, CancellationToken ct)
        => db.KycFiles
            .IgnoreQueryFilters()
            .Where(f => f.TenantId == tenantId && f.CustomerId == customerEntityId)
            .AllAsync(f => !HoldingStatuses.Contains(f.Status), ct);

    /// <summary>The statuses in which a file still needs its evidence — see <see cref="IsRetentionClearedAsync"/>.</summary>
    private static readonly List<KycFileStatus> HoldingStatuses =
    [
        KycFileStatus.Collecting,
        KycFileStatus.Verifying,
        KycFileStatus.Validating,
        KycFileStatus.ComplementRequired,
        KycFileStatus.UnderReview,
        KycFileStatus.Suspended,
    ];

    /// <summary>
    /// Cache key for a customer's ceilings, stamped with the tenant's cache GENERATION.
    ///
    /// <para>
    /// The generation exists because a ceiling is now writable over HTTP, and a distributed cache
    /// cannot be asked to drop "every entry of this tenant": the keys are per customer and nothing
    /// enumerates them. Changing the generation makes all of them unreachable in one write, so a
    /// lowered ceiling bites immediately instead of when the last entry expires — up to five minutes
    /// during which operations would be measured against the policy the administrator just revoked.
    /// </para>
    /// </summary>
    internal static string LimitsCacheKey(Guid tenantId, Guid customerEntityId, string generation)
        => $"kyc:limits:{generation}:{tenantId}:{customerEntityId}";

    /// <summary>Where the tenant's cache generation is stored. Written without expiration.</summary>
    internal static string LimitsGenerationKey(Guid tenantId) => $"kyc:limits-gen:{tenantId}";

    /// <summary>
    /// The tenant's current generation, or <c>"0"</c> when none has ever been written. A lost
    /// generation entry is harmless: unknown keys are a cache MISS, and a miss recomputes.
    /// </summary>
    internal static async Task<string> ReadLimitsGenerationAsync(
        IDistributedCache cache, Guid tenantId, CancellationToken ct)
        => await cache.GetStringAsync(LimitsGenerationKey(tenantId), ct) ?? "0";

    /// <summary>
    /// Drops every cached ceiling of a tenant at once, by moving its generation. Called when a
    /// parameter that feeds <see cref="KycLimits"/> is written — see
    /// <c>KycSettingKeys.AffectsLimits</c>.
    ///
    /// <para>
    /// A fresh token rather than an incremented counter: two administrators saving at the same
    /// moment would read the same counter and write the same successor, leaving one of the two
    /// invalidations without effect.
    /// </para>
    /// </summary>
    internal static Task InvalidateTenantLimitsAsync(
        IDistributedCache cache, Guid tenantId, CancellationToken ct)
        => cache.SetStringAsync(
            LimitsGenerationKey(tenantId),
            Guid.NewGuid().ToString("N"),
            // No expiration: if this entry vanished, the generation would fall back to "0" and a
            // pre-invalidation entry could still be served for the rest of its own five minutes.
            new DistributedCacheEntryOptions(),
            ct);

    public async Task<KycLimits?> GetLimitsAsync(
        Guid tenantId, Guid customerEntityId, CancellationToken ct)
    {
        var generation = await ReadLimitsGenerationAsync(cache, tenantId, ct);
        var key = LimitsCacheKey(tenantId, customerEntityId, generation);

        var cached = await cache.GetStringAsync(key, ct);
        if (cached is not null)
            return JsonSerializer.Deserialize<KycLimits>(cached);

        var file = await db.KycFiles
            .IgnoreQueryFilters()
            .Where(f => f.TenantId == tenantId && f.CustomerId == customerEntityId)
            .OrderByDescending(f => f.Status != KycFileStatus.Rejected && f.Status != KycFileStatus.Suspended)
            .ThenByDescending(f => f.UpdatedAt)
            .Select(f => new { f.Status, f.Tier })
            .FirstOrDefaultAsync(ct);

        // No file at all: the caller must read this as "may not operate", not as "no limits".
        if (file is null) return null;

        // A closed file grants nothing.
        if (file.Status is KycFileStatus.Rejected or KycFileStatus.Suspended) return null;

        // An EXPIRED file falls back to the simplified ceilings rather than cutting the customer
        // off: the relationship is still valid, the review is simply overdue. A full customer
        // whose review lapsed is capped, not blocked.
        var capped = file.Status == KycFileStatus.Expired || file.Tier != KycTier.Full;

        var limits = new KycLimits(
            Tier: capped ? nameof(KycTier.Simplified) : nameof(KycTier.Full),
            IsCapped: capped,
            MaxBalance: await settings.GetDecimalAsync(tenantId, KycSettingKeys.SimplifiedMaxBalance, ct),
            MaxFlow: await settings.GetDecimalAsync(tenantId, KycSettingKeys.SimplifiedMaxMonthlyFlow, ct),
            WindowDays: await settings.GetIntAsync(tenantId, KycSettingKeys.SimplifiedFlowWindowDays, ct),
            AlertPct: await settings.GetIntAsync(tenantId, KycSettingKeys.SimplifiedAlertPct, ct));

        // Five minutes, and invalidated on KycTierChanged. The TTL is the floor, not the
        // mechanism: a downgrade must take effect at once, which is the consumer's job.
        await cache.SetStringAsync(
            key,
            JsonSerializer.Serialize(limits),
            new DistributedCacheEntryOptions { AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(5) },
            ct);

        return limits;
    }

    /// <summary>
    /// Always <see cref="KycFlowUsage.Unknown"/> — and that is the honest answer today.
    ///
    /// The flow is the sum of deposits and withdrawals across every account of the customer over a
    /// rolling window. Nothing in this solution owns an account, a balance or a transaction: M03
    /// and M07 do not exist. Returning zero would be worse than returning nothing, because a
    /// caller would read "nothing consumed" and let an operation through on a ceiling it never
    /// checked. <c>Known = false</c> forces the caller to refuse instead.
    /// </summary>
    public Task<KycFlowUsage> GetFlowUsageAsync(
        Guid tenantId, Guid customerEntityId, CancellationToken ct)
    {
        logger.LogDebug(
            "Flow usage asked for customer {CustomerId}; no module owns transactions yet, answering Unknown",
            customerEntityId);

        return Task.FromResult(KycFlowUsage.Unknown);
    }

    public async Task<IReadOnlyDictionary<Guid, KycJourneySummary>> GetJourneySummariesAsync(
        Guid tenantId, IReadOnlyCollection<Guid> customerIds, CancellationToken ct)
    {
        if (customerIds.Count == 0) return new Dictionary<Guid, KycJourneySummary>();
        var ids = customerIds.Distinct().ToList();

        // Même choix de dossier que GetStatusAsync : l'ouvert d'abord, sinon le plus récent.
        var files = (await db.KycFiles
                .IgnoreQueryFilters()
                .Where(f => f.TenantId == tenantId && ids.Contains(f.CustomerId))
                .Select(f => new { f.Id, f.CustomerId, f.Status, f.ConfidenceScore, f.UpdatedAt })
                .ToListAsync(ct))
            .GroupBy(f => f.CustomerId)
            .Select(g => g
                .OrderByDescending(f => f.Status != KycFileStatus.Rejected && f.Status != KycFileStatus.Suspended)
                .ThenByDescending(f => f.UpdatedAt)
                .First())
            .ToList();
        if (files.Count == 0) return new Dictionary<Guid, KycJourneySummary>();

        var fileIds = files.Select(f => f.Id).ToList();
        var identityRead = (await db.KycIdentityDocuments.IgnoreQueryFilters()
                .Where(d => fileIds.Contains(d.KycFileId) && (d.OcrFieldsJson != null || d.MrzDataJson != null))
                .Select(d => d.KycFileId).Distinct().ToListAsync(ct))
            .ToHashSet();
        var faceMatched = (await db.KycFaceVerifications.IgnoreQueryFilters()
                .Where(v => fileIds.Contains(v.KycFileId) && v.IsMatch)
                .Select(v => v.KycFileId).Distinct().ToListAsync(ct))
            .ToHashSet();
        var proofOfAddress = (await db.KycDocuments.IgnoreQueryFilters()
                .Where(d => fileIds.Contains(d.KycFileId)
                         && d.Kind == Infrastructure.Storage.KycDocumentKind.ProofOfAddress
                         && d.ReviewDecision != KycDocumentReviewDecision.Refused)
                .Select(d => d.KycFileId).Distinct().ToListAsync(ct))
            .ToHashSet();

        return files.ToDictionary(f => f.CustomerId, f => new KycJourneySummary(
            f.CustomerId,
            f.Id,
            f.Status.ToString(),
            CompletedSteps(f.Status),
            identityRead.Contains(f.Id),
            faceMatched.Contains(f.Id),
            proofOfAddress.Contains(f.Id),
            AmlCheckCompleted: f.Status is KycFileStatus.Simplified or KycFileStatus.Full
                or KycFileStatus.UnderReview or KycFileStatus.Expired,
            f.ConfidenceScore));
    }

    /// <summary>
    /// Trois temps : collecte, vérification, validation complète. Un KYC simplifié est validé
    /// mais plafonné — il lui reste le troisième temps, d'où 2.
    /// </summary>
    internal static int CompletedSteps(KycFileStatus status) => status switch
    {
        KycFileStatus.Collecting or KycFileStatus.ComplementRequired => 1,
        KycFileStatus.Verifying or KycFileStatus.Validating => 2,
        KycFileStatus.Simplified or KycFileStatus.Expired => 2,
        KycFileStatus.Full or KycFileStatus.UnderReview => 3,
        _ => 1,
    };
}
