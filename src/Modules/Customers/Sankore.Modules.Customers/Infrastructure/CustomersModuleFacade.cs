namespace Sankore.Modules.Customers.Infrastructure;

using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Customers.Domain;
using Sankore.Modules.Customers.PublicApi;
using Sankore.Shared.Kernel;

/// <summary>
/// The single implementation of <see cref="ICustomersModule"/> — everything other
/// modules are allowed to ask M01 synchronously.
///
/// All reads use <c>IgnoreQueryFilters()</c> with an explicit tenant predicate: the
/// caller may be a Hangfire job or a MassTransit consumer whose ambient
/// <c>ITenantContext</c> is not the tenant in the request, and in that situation the
/// global filter would silently return nothing (or, worse, be satisfied by the wrong
/// tenant). The predicate is never omitted.
///
/// Nothing on this facade decrypts a protected field. <see cref="ClientSummary"/> holds
/// identifiers, a display name and enum names only; a module that genuinely needs a
/// phone number or a document number must go through M01's audited reveal endpoint so
/// the access is logged against a named user.
/// </summary>
internal sealed class CustomersModuleFacade(CustomersDbContext db, ILeadConversionService leadConversion)
    : ICustomersModule
{
    /// <summary>
    /// Depth limit while walking the merge chain. A chain is normally one or two hops;
    /// the cap turns a corrupted cycle (A merged into B merged into A) into a bounded
    /// wrong answer instead of an infinite loop holding a request thread.
    /// </summary>
    private const int MaxMergeHops = 20;

    public async Task<ClientSummary?> GetClientSummaryAsync(Guid tenantId, Guid clientId, CancellationToken ct)
        => await db.Clients
            .IgnoreQueryFilters()
            .Where(c => c.TenantId == tenantId && c.Id == clientId)
            .Select(c => new ClientSummary(
                c.Id,
                c.ClientNumber,
                c.Type.ToString(),
                c.DisplayName,
                c.Status.ToString(),
                c.AgencyId,
                c.AdvisorUserId,
                c.KycStatus.ToString(),
                c.RiskLevel.ToString(),
                c.MergedIntoId))
            .FirstOrDefaultAsync(ct);

    public async Task<IReadOnlyDictionary<Guid, ClientSummary>> GetClientSummariesAsync(
        Guid tenantId, IReadOnlyCollection<Guid> clientIds, CancellationToken ct)
    {
        // A List, not the incoming collection and not an array: on .NET 10 an array's Contains
        // binds to the ReadOnlySpan<T> extension and no longer translates to SQL, which would
        // turn this into a client-side evaluation over every client in the tenant.
        var ids = clientIds.Distinct().ToList();

        // No round-trip for nothing: a page whose rows carry no customer reference would otherwise
        // issue `IN ()`.
        if (ids.Count == 0)
            return new Dictionary<Guid, ClientSummary>();

        var rows = await db.Clients
            .IgnoreQueryFilters()
            .Where(c => c.TenantId == tenantId && ids.Contains(c.Id))
            .Select(c => new ClientSummary(
                c.Id,
                c.ClientNumber,
                c.Type.ToString(),
                DisplayName: (c.Type == ClientType.Individual ? c.DisplayName : c.LegalName)!,
                c.Status.ToString(),
                c.AgencyId,
                c.AdvisorUserId,
                c.KycStatus.ToString(),
                c.RiskLevel.ToString(),
                c.MergedIntoId))
            .ToListAsync(ct);

        return rows.ToDictionary(r => r.Id);
    }

    public async Task<Guid?> ResolveClientIdAsync(Guid tenantId, Guid clientId, CancellationToken ct)
    {
        var current = clientId;
        Guid? deepestKnown = null;

        for (var hop = 0; hop < MaxMergeHops; hop++)
        {
            var node = await db.Clients
                .IgnoreQueryFilters()
                .Where(c => c.TenantId == tenantId && c.Id == current)
                .Select(c => new { c.Id, c.MergedIntoId })
                .FirstOrDefaultAsync(ct);

            // Nothing at the first hop means the client simply does not exist.
            // Nothing at a later hop means the chain dangles, so the best answer is
            // the last record we did find rather than a misleading null.
            if (node is null)
                return deepestKnown;

            deepestKnown = node.Id;

            if (node.MergedIntoId is null || node.MergedIntoId == node.Id)
                return node.Id;

            current = node.MergedIntoId.Value;
        }

        // Chain longer than the cap, or cyclic: return the furthest record reached.
        return deepestKnown;
    }

    public Task<bool> ExistsAndActiveAsync(Guid tenantId, Guid clientId, CancellationToken ct)
        => db.Clients
            .IgnoreQueryFilters()
            .AnyAsync(
                c => c.TenantId == tenantId
                  && c.Id == clientId
                  && c.Status == ClientStatus.Active,
                ct);

    public Task<Result<CreateFromLeadResult>> CreateFromLeadAsync(CreateFromLeadRequest request, CancellationToken ct)
        => leadConversion.CreateFromLeadAsync(request, ct);

    /// <summary>
    /// The legacy contract says "exists"; the honest mapping is "exists and is usable".
    /// Its only caller gates conversion on it, and attaching business to a suspended,
    /// archived or merged record would be wrong — so an inactive client answers false.
    /// </summary>
    public Task<bool> ExistsAsync(Guid tenantId, Guid customerId, CancellationToken ct)
        => ExistsAndActiveAsync(tenantId, customerId, ct);

    /// <summary>
    /// Email and PhoneNumber are deliberately left null: both are encrypted at rest in
    /// M01 and only reachable through the audited reveal endpoint. No sensitive data
    /// crosses this boundary — a legacy caller that needs a contact detail has to ask
    /// for it explicitly, under its own user's identity, and be logged doing so.
    /// </summary>
    public async Task<CustomerSummary?> GetCustomerAsync(Guid tenantId, Guid customerId, CancellationToken ct)
    {
        var summary = await GetClientSummaryAsync(tenantId, customerId, ct);

        return summary is null
            ? null
            : new CustomerSummary(
                Id: summary.Id,
                DisplayName: summary.DisplayName,
                Email: null,
                PhoneNumber: null,
                AgencyId: summary.AgencyId);
    }

}
