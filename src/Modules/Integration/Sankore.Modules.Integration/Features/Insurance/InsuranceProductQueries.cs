namespace Sankore.Modules.Integration.Features.Insurance;

using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Integration.Domain;
using Sankore.Modules.Integration.Infrastructure;
using Sankore.Modules.Integration.PublicApi;

/// <summary>
/// Reads shared by the product slices. No tenant predicate anywhere: the DbContext's global query
/// filter scopes every one of these, which is what makes another tenant's product a 404 and not a
/// 403.
/// </summary>
internal static class InsuranceProductQueries
{
    /// <summary>
    /// One product and the connection it belongs to, or <c>null</c>.
    ///
    /// <para>
    /// Both rows in one read. The connection is not optional information: every DTO carries its
    /// name and its active flag, and ASS-03's fourth criterion cannot be answered without it — so
    /// a handler that forgot it would render a product whose offerability it had guessed.
    /// </para>
    /// </summary>
    internal static async Task<(InsuranceProduct Product, IntegrationConnection Connection)?> FindWithConnectionAsync(
        this IntegrationDbContext db, Guid productId, CancellationToken ct, bool tracking = false)
    {
        var products = tracking ? db.InsuranceProducts.AsTracking() : db.InsuranceProducts;

        // Anonymous, not a named record: EF cannot see through a custom construction in a
        // projection it then has to order or filter on — see ListInsuranceProductsHandler for the
        // run-time failure that shape produces.
        var row = await (
            from p in products
            join c in db.Connections on p.ConnectionId equals c.Id
            where p.Id == productId
            select new { Product = p, Connection = c }).FirstOrDefaultAsync(ct);

        return row is null ? null : (row.Product, row.Connection);
    }

    /// <summary>
    /// The tenant's active INSURANCE connection with that id, or <c>null</c>.
    ///
    /// <para>
    /// The family check is here and not left to the caller: a catalogue entry created against a
    /// core-banking connection would resolve a CBS adapter when ASS-04 came to subscribe it, and
    /// the error would surface at the counter rather than on the configuration screen. Activity is
    /// NOT required — a product may legitimately be configured before its connection is activated,
    /// which is why offerability is computed and not refused here.
    /// </para>
    /// </summary>
    internal static Task<IntegrationConnection?> FindInsuranceConnectionAsync(
        this IntegrationDbContext db, Guid connectionId, CancellationToken ct)
        => db.Connections
            .Where(c => c.Id == connectionId && c.Family == IntegrationFamily.Insurance)
            .FirstOrDefaultAsync(ct);
}

/// <summary>
/// Answers whether an insurer can price a product itself — <c>IntegrationCapability.PriceProduct</c>
/// on the adapter serving the connection (ASS-03, criterion 2).
///
/// <para>
/// A service and not a static helper because resolving an adapter is a keyed DI lookup, and a
/// cache because a page of products belongs to a handful of connections: without it the catalogue
/// screen would resolve the same adapter once per row.
/// </para>
///
/// <para>
/// A connection with no registered adapter answers <c>false</c>, not an exception. That is the
/// honest verdict — a product whose insurer cannot be reached cannot be priced by it — and it
/// shows up as <c>INSURER_PRICING_UNAVAILABLE</c> on the row rather than as a 500 on the screen.
/// </para>
///
/// <para>
/// <b>Per connection, and genuinely so.</b> The cache is keyed by connection and so is the value
/// it holds: <see cref="ResolvedAdapter.Capabilities"/> applies
/// <c>ICbsAdapter.CapabilitiesFor</c> to the row being asked about. That used to be impossible —
/// the contract's capability property took no connection, so an adapter was asked "what can this
/// KIND do" and any adapter whose matrix depends on the row answered for whichever one it had
/// bound itself to. The consequence landed exactly here: with ORASS Vie supporting
/// <c>PriceProduct</c> and ORASS IARD not, an IARD product was reported offerable and its quote
/// then failed at the counter with <c>CAPABILITY_NOT_SUPPORTED</c> — the one outcome
/// <c>IntegrationCapabilities</c> exists to prevent. Pinned by
/// <c>InsurerPricingProbeTests.Two_connections_of_the_same_kind_each_get_their_own_answer</c>,
/// a test that could not be written before the contract carried its subject.
/// </para>
/// </summary>
internal sealed class InsurerPricingProbe(IntegrationAdapterResolver resolver)
{
    private readonly Dictionary<Guid, bool> _cache = [];

    internal bool CanPrice(IntegrationConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);

        if (_cache.TryGetValue(connection.Id, out var cached)) return cached;

        var adapter = resolver.ResolveFor(connection);

        var canPrice = adapter.IsSuccess
                       && adapter.Value.Capabilities.Supports(IntegrationCapability.PriceProduct);

        _cache[connection.Id] = canPrice;
        return canPrice;
    }
}
