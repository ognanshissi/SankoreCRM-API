namespace Sankore.Modules.Customers.Features.Holdings.Shared;

using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Administration.PublicApi;
using Sankore.Modules.Customers.Domain;
using Sankore.Modules.Customers.Infrastructure;
using Sankore.Shared.Infrastructure.Auth;
using Sankore.Shared.Kernel;
using Sankore.Shared.Kernel.Authorization;

/// <summary>
/// The gates every holding write runs, in the one order they have to happen (BNF-B-14).
///
/// <para>
/// A service and not a base class, like M13's household context and for the same reason: the order
/// matters. The agency perimeter before the product line, because telling an out-of-perimeter
/// caller which line a holding is on is already a leak; the line before the aggregate, because the
/// roles it carries are an argument <c>Designate</c> needs.
/// </para>
/// </summary>
internal sealed class HoldingWriteContext(
    CustomersDbContext db,
    ICurrentUser currentUser,
    IAgencyScopeProvider agencyScope,
    IAdministrationModule administration,
    TimeProvider clock)
{
    internal TimeProvider Clock => clock;
    internal Guid Actor => currentUser.Id;

    /// <summary>
    /// Loads a holding WITH its designations, tracked, after the perimeter check on its client.
    /// </summary>
    /// <remarks>
    /// <c>Include</c> is mandatory and not an optimisation: <c>Designate</c> and
    /// <c>FlagDesignationsOf</c> both walk the collection, and a holding loaded without it would
    /// see an EMPTY clause — which would pass a MinCount rule it should fail and would close no
    /// previous version when writing an avenant.
    /// </remarks>
    internal async Task<Result<ProductHolding>> LoadAsync(Guid holdingId, CancellationToken ct)
    {
        var holding = await db.ProductHoldings
            .AsTracking()
            .Include(h => h.Designations)
            .FirstOrDefaultAsync(h => h.Id == holdingId, ct);

        if (holding is null)
            return Result.Fail<ProductHolding>(CustomerErrors.HoldingNotFound);

        var perimeter = await EnsureClientInScopeAsync(holding.ClientId, ct);
        if (perimeter.IsFailure)
            // The holding's own code, not the client's: the caller asked about a holding, and
            // answering CLIENT_NOT_FOUND for one that exists would be a different lie.
            return Result.Fail<ProductHolding>(CustomerErrors.HoldingNotFound);

        return Result.Ok(holding);
    }

    /// <summary>
    /// Whether the caller may act on this client at all. Out of perimeter reads as missing, never
    /// 403 — the module's rule, so a response cannot be used to probe for existence.
    /// </summary>
    internal async Task<Result<Client>> EnsureClientInScopeAsync(
        Guid clientId, CancellationToken ct)
    {
        var client = await db.Clients.FirstOrDefaultAsync(c => c.Id == clientId, ct);

        if (client is null ||
            !await agencyScope.CanAccessAgencyAsync(currentUser.TenantId, currentUser.Id, client.AgencyId, ct))
            return Result.Fail<Client>(CustomerErrors.ClientNotFound);

        if (client.IsReadOnly)
            return Result.Fail<Client>(CustomerErrors.ClientReadOnly);

        return Result.Ok(client);
    }

    /// <summary>
    /// The product line's policy — its code, its category, its designation roles and whether it may
    /// be held more than once.
    /// </summary>
    /// <remarks>
    /// <b>An M12 outage FAILS the write.</b> Deliberately unlike the best-effort lookups on M13's
    /// capture path: a subscription is a deliberate act the agent can repeat, whereas letting it
    /// through would store a holding with no category, no roles and no multiple-holding rule — and
    /// the designation gate would then accept anything.
    ///
    /// <para>
    /// A null policy means the line does not exist in this tenant, which the contract distinguishes
    /// from a line that exists and collects nothing. Here it is a REFUSAL rather than a graceful
    /// degradation, unlike on a lead: a lead's product line is a best-effort resolution of a label,
    /// while this one is a catalogue id a caller chose.
    /// </para>
    /// </remarks>
    internal async Task<Result<BeneficiaryPolicyDto>> ReadLineAsync(
        Guid tenantId, Guid productLineId, CancellationToken ct)
    {
        if (productLineId == Guid.Empty)
            return Result.Fail<BeneficiaryPolicyDto>(CustomerErrors.ProductLineNotFound);

        try
        {
            // TransactionBehavior holds an ambient System.Transactions scope around this command,
            // and M12 reads through its own DbContext: two connections enlisting in one scope
            // promote to a distributed transaction, which Npgsql does not support.
            using var suppressed = new System.Transactions.TransactionScope(
                System.Transactions.TransactionScopeOption.Suppress,
                System.Transactions.TransactionScopeAsyncFlowOption.Enabled);

            var policy = await administration.GetBeneficiaryPolicyAsync(tenantId, productLineId, ct);
            suppressed.Complete();

            return policy is null
                ? Result.Fail<BeneficiaryPolicyDto>(CustomerErrors.ProductLineNotFound)
                : Result.Ok(policy);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return Result.Fail<BeneficiaryPolicyDto>(CustomerErrors.ProductLineUnavailable);
        }
    }

    /// <summary>
    /// The roles a holding's line declares, read for a designation write.
    /// </summary>
    /// <remarks>
    /// Reached through the holding's <c>ProductLineId</c>, which may now be DANGLING — a product
    /// retired after the subscription. That is a refusal and not an empty role list: an empty list
    /// would make <c>Designate</c> reject every role, which looks like a configuration error, while
    /// <c>PRODUCT_LINE_NOT_FOUND</c> says what actually happened.
    /// </remarks>
    internal Task<Result<BeneficiaryPolicyDto>> ReadLineOfAsync(
        ProductHolding holding, CancellationToken ct)
        => ReadLineAsync(holding.TenantId, holding.ProductLineId, ct);

    internal Task SaveAsync(CancellationToken ct) => db.SaveChangesAsync(ct);
}
