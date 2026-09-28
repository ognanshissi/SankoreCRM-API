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
}
