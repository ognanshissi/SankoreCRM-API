namespace Sankore.Modules.Integration.Features.Balance.GetLiveBalance;

using MediatR;
using Sankore.Modules.Customers.PublicApi;
using Sankore.Modules.Integration.PublicApi;
using Sankore.Shared.Infrastructure.Auth;
using Sankore.Shared.Kernel;
using Sankore.Shared.Kernel.Authorization;

/// <summary>
/// "What is on this account, right now?" — INT-15 over HTTP.
///
/// <para>
/// A query and not an <c>ICommand</c>: it mutates nothing, so it takes neither a transaction nor
/// an audit row. The call that leaves for the core banking system is journalled by INT-08's call
/// log, where it belongs — one row per outbound call, with its latency and its outcome, rather
/// than one audit entry per counter screen.
/// </para>
/// </summary>
internal sealed record GetLiveBalanceQuery(Guid CrmCustomerId, string AccountRef)
    : IRequest<Result<LiveBalanceDto>>;

/// <summary>
/// <paramref name="IsStale"/> is the field that matters at a counter. <c>false</c> means the core
/// banking system answered just now (or within the last 60 seconds, which is the same figure);
/// <c>true</c> means the CBS could not be reached and this is the last synchronised value, true as
/// of <paramref name="AsOf"/>. A clerk shown a stale figure as if it were current will quote it to
/// the customer, so the flag travels with the number and is never inferred by the front-end.
/// </summary>
internal sealed record LiveBalanceDto(
    Guid CrmCustomerId,
    string AccountId,
    string Currency,
    decimal Balance,
    decimal? AvailableBalance,
    DateTimeOffset AsOf,
    bool IsStale);

internal sealed class GetLiveBalanceHandler(
    IIntegrationModule integration,
    ICustomersModule customers,
    IAgencyScopeProvider agencyScope,
    ICurrentUser currentUser)
    : IRequestHandler<GetLiveBalanceQuery, Result<LiveBalanceDto>>
{
    public async Task<Result<LiveBalanceDto>> Handle(
        GetLiveBalanceQuery request, CancellationToken ct)
    {
        var tenantId = currentUser.TenantId;

        // The client's agency comes from M01 through its PublicApi contract — this module owns no
        // customer record and must not grow one. ClientSummary carries no sensitive field.
        var client = await customers.GetClientSummaryAsync(tenantId, request.CrmCustomerId, ct);

        if (client is null)
            return Result.Fail<LiveBalanceDto>(BalanceErrors.BalanceNotAvailable);

        // INT-15 criterion 3: restricted to the clients of the agent's own agency. The perimeter
        // is the user's agency plus its active descendants plus any agency granted by a
        // PermissionAttribution; CanAccessAgencyAsync answers true for an unrestricted caller (a
        // super-user), which is what IAgencyScopeProvider's null set means.
        //
        // A client outside the perimeter answers NOT FOUND and never FORBIDDEN. A 403 would
        // confirm that this customer exists in some other branch of the network, and the
        // existence of a client is itself information the perimeter is there to withhold — a
        // counter clerk could enumerate another branch's book one id at a time. Same rule as
        // M01's GetClient and M02's KYC file read.
        var inPerimeter = await agencyScope.CanAccessAgencyAsync(
            tenantId, currentUser.Id, client.AgencyId, ct);

        if (!inPerimeter)
            return Result.Fail<LiveBalanceDto>(BalanceErrors.BalanceNotAvailable);

        // The facade owns the rest, and owns it for every caller: that the account belongs to this
        // customer (§5bis(c) of the module plan), the cache, the breaker and the stale fallback.
        // Nothing of that is duplicated here — a second copy of an ownership check is a second
        // copy that can drift.
        var balance = await integration.CoreBanking.GetLiveBalanceAsync(
            request.CrmCustomerId, request.AccountRef, ct);

        // Null covers "not this customer's account" and "no figure anywhere" — never a fabricated
        // zero, which at a counter is indistinguishable from an empty account.
        if (balance is null)
            return Result.Fail<LiveBalanceDto>(BalanceErrors.BalanceNotAvailable);

        return Result.Ok(new LiveBalanceDto(
            CrmCustomerId: request.CrmCustomerId,
            AccountId: balance.AccountId.Value,
            Currency: balance.Currency,
            Balance: balance.Balance,
            AvailableBalance: balance.AvailableBalance,
            AsOf: balance.AsOf,
            IsStale: balance.IsStale));
    }
}
