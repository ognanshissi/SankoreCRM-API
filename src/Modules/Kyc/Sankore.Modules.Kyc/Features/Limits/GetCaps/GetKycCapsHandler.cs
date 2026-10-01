namespace Sankore.Modules.Kyc.Features.Limits.GetCaps;

using MediatR;
using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Kyc.Domain;
using Sankore.Modules.Kyc.Infrastructure;
using Sankore.Modules.Kyc.Infrastructure.Settings;
using Sankore.Modules.Kyc.PublicApi;
using Sankore.Shared.Infrastructure.Auth;
using Sankore.Shared.Kernel;
using Sankore.Shared.Kernel.Authorization;

/// <summary>
/// Reads the ceilings through <see cref="IKycModule.GetLimitsAsync"/> rather than recomputing them.
///
/// <para>
/// That matters: which tier a file grants, that a closed file grants nothing and that an EXPIRED
/// file falls back to the simplified ceilings instead of blocking the customer are decisions with
/// exactly one home. A second copy here would drift, and the direction it drifts in is "the screen
/// says uncapped while the enforcement says capped".
/// </para>
///
/// <para>
/// What this handler adds is the agency perimeter. The facade deliberately bypasses every query
/// filter — it answers background jobs of other modules that carry no ambient tenant — so an HTTP
/// read must gate it itself, or <c>kyc:read</c> plus a customer id would expose another branch's
/// compliance position.
/// </para>
/// </summary>
internal sealed class GetKycCapsHandler(
    KycDbContext db,
    ICurrentUser currentUser,
    IAgencyScopeProvider agencyScope,
    IKycModule kyc,
    IKycSettings settings)
    : IRequestHandler<GetKycCapsQuery, Result<KycCapsDto>>
{
    public async Task<Result<KycCapsDto>> Handle(GetKycCapsQuery request, CancellationToken ct)
    {
        // The global query filter scopes this to the caller's tenant. Closed files are excluded the
        // same way the by-customer file read excludes them: they are evidence, not a live position.
        var query = db.KycFiles
            .Where(f => f.CustomerId == request.CustomerId
                     && f.Status != KycFileStatus.Rejected
                     && f.Status != KycFileStatus.Suspended);

        var perimeter = await agencyScope.GetAccessibleAgencyIdsAsync(
            currentUser.TenantId, currentUser.Id, ct);

        if (perimeter is not null)
        {
            // Materialised as a List: on .NET 10 an array's Contains binds to the ReadOnlySpan<T>
            // extension and no longer translates to SQL. A List does.
            var accessible = perimeter.ToList();

            // A file with no agency is invisible to a restricted caller — failing closed, so an
            // unresolved agency never becomes everyone's.
            query = query.Where(f => f.AgencyId != null && accessible.Contains(f.AgencyId.Value));
        }

        var kycFileId = await query
            .OrderByDescending(f => f.UpdatedAt)
            .Select(f => (Guid?)f.Id)
            .FirstOrDefaultAsync(ct);

        // 404 and never 403, outside the perimeter as well as outside the tenant: a refusal would
        // confirm that this customer has a KYC file here.
        if (kycFileId is null)
            return Result.Fail<KycCapsDto>(KycErrors.FileNotFound);

        var limits = await kyc.GetLimitsAsync(currentUser.TenantId, request.CustomerId, ct);

        // Null means "may not operate", which from a read's point of view is the same answer as no
        // file: the caller learns nothing it could mistake for "no limits apply".
        if (limits is null)
            return Result.Fail<KycCapsDto>(KycErrors.FileNotFound);

        var usage = await kyc.GetFlowUsageAsync(currentUser.TenantId, request.CustomerId, ct);

        // The flow may one day be measurable; the balance cannot be measured at all today, since no
        // module owns an account. Both are reported as nulls with a reason rather than as zeros.
        decimal? flow = usage.Known ? usage.Consumed : null;
        decimal? balance = null;

        var currency = await settings.GetStringAsync(
            currentUser.TenantId, KycSettingKeys.CapsCurrency, ct);

        return Result.Ok(new KycCapsDto(
            CustomerId: request.CustomerId,
            KycFileId: kycFileId.Value,
            Tier: limits.Tier,
            IsCapped: limits.IsCapped,
            Currency: currency,
            // Nulls for an uncapped customer: the contract says the amounts carry no meaning, and a
            // number a gauge can be drawn against would invent a ceiling the enforcement side does
            // not apply.
            BalanceCap: limits.IsCapped ? limits.MaxBalance : null,
            FlowCap: limits.IsCapped ? limits.MaxFlow : null,
            FlowWindowDays: limits.IsCapped ? limits.WindowDays : null,
            AlertPct: limits.IsCapped ? limits.AlertPct : null,
            Usage: new KycCapsUsageDto(
                Balance: balance,
                Flow: flow,
                FlowWindowStart: usage.Known ? usage.WindowStart : null,
                UnavailableReason: balance is null || flow is null
                    ? KycCapsUsageReasons.NoTransactionSource
                    : null)));
    }
}
