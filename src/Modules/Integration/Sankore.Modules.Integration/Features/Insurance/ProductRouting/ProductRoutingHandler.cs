namespace Sankore.Modules.Integration.Features.Insurance.ProductRouting;

using MediatR;
using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Administration.PublicApi;
using Sankore.Modules.Integration.Domain;
using Sankore.Modules.Integration.Infrastructure;
using Sankore.Modules.Integration.PublicApi;
using Sankore.Shared.Kernel;

internal sealed class ProductRoutingHandler(
    IntegrationDbContext db,
    IAdministrationModule administration,
    ITenantContext tenant)
    : IRequestHandler<ProductRoutingQuery, Result<ProductRoutingDto>>
{
    /// <summary>
    /// The three categories of M12's catalogue an insurance agreement may realise — the same list
    /// <c>CrmProductCatalogue</c> holds, and for the same reason: accepting only
    /// <c>Insurance</c> would ignore a health cover for having been described precisely.
    /// </summary>
    private static readonly string[] InsuranceCategories =
    [
        ProductCategory.Insurance.ToString(),
        ProductCategory.HealthInsurance.ToString(),
        ProductCategory.ForecastInsurance.ToString(),
    ];

    public async Task<Result<ProductRoutingDto>> Handle(
        ProductRoutingQuery query, CancellationToken ct)
    {
        var tenantId = tenant.CurrentTenantId;

        // Active connections only, exactly as ResolveInsurerForProductAsync selects them: an
        // inactive insurer does not receive a subscription, so counting it would report a
        // fallback that will not happen.
        var connections = await db.Connections
            .Where(c => c.Family == IntegrationFamily.Insurance && c.IsActive)
            .Select(c => new { c.Id, c.Name })
            .ToListAsync(ct);

        // One call per category rather than ListProductsAsync(null) filtered here: the contract
        // refuses an unrecognised category with an EMPTY list rather than every product, so
        // asking by name is the shape that cannot silently widen.
        var lines = new List<ProductSummary>();
        foreach (var category in InsuranceCategories)
            lines.AddRange(await administration.ListProductsAsync(tenantId, category, ct));

        // Active lines only. A withdrawn product takes no new subscription, so its mapping is not
        // an operational fault — and reporting it would bury the lines that are.
        var active = lines.Where(p => p.IsActive).ToList();

        if (active.Count == 0)
        {
            return Result.Ok(new ProductRoutingDto(
                ActiveInsuranceConnections: connections.Count,
                UnroutableCount: 0,
                Lines: []));
        }

        // crm_code carries the product's ID, which is what ResolveInsurerForProductAsync compares
        // (`crmProductId.ToString()`). A mapping keyed on the product CODE looks configured and
        // resolves nothing, which is why the expected value travels in the response.
        var crmCodes = active.Select(p => p.Id.ToString()).ToList();
        var connectionIds = connections.Select(c => c.Id).ToList();

        // A List and not an array on both sides: on .NET 10 an array's Contains binds to the
        // ReadOnlySpan<T> extension and no longer translates to SQL.
        var mappings = await db.Mappings
            .Where(m => m.Domain == MappingDomain.Product
                     && crmCodes.Contains(m.CrmCode)
                     && connectionIds.Contains(m.ConnectionId))
            .Select(m => new { m.CrmCode, m.ConnectionId })
            .ToListAsync(ct);

        // First wins, like the resolution itself: it reads with FirstOrDefaultAsync, so a line
        // mapped to two connections routes to one of them and this report must say the same
        // thing rather than a tidier one.
        var byCrmCode = mappings
            .GroupBy(m => m.CrmCode, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First().ConnectionId, StringComparer.Ordinal);

        var result = new List<ProductRoutingLineDto>(active.Count);

        foreach (var line in active)
        {
            var crmCode = line.Id.ToString();

            var (routability, connectionId) = Resolve(crmCode, connectionIds, byCrmCode);

            result.Add(new ProductRoutingLineDto(
                ProductLineId: line.Id,
                Code: line.Code,
                Name: line.Name,
                Category: line.Category,
                CrmCode: crmCode,
                Routability: routability,
                ConnectionId: connectionId,
                ConnectionName: connectionId is { } id
                    ? connections.First(c => c.Id == id).Name
                    : null));
        }

        return Result.Ok(new ProductRoutingDto(
            ActiveInsuranceConnections: connections.Count,
            UnroutableCount: result.Count(l => l.Routability == ProductRoutability.Unroutable),
            Lines: result.OrderBy(l => l.Code, StringComparer.Ordinal).ToList()));
    }

    /// <summary>
    /// The resolution of <c>ResolveInsurerForProductAsync</c>, in the same order, reported instead
    /// of performed. The order is the logic: no connection at all is not a mapping problem; a
    /// mapping wins; a single connection is the documented fallback; several connections with no
    /// mapping is the refusal.
    /// </summary>
    private static (ProductRoutability, Guid?) Resolve(
        string crmCode,
        List<Guid> connectionIds,
        Dictionary<string, Guid> byCrmCode)
    {
        if (connectionIds.Count == 0)
            return (ProductRoutability.NoInsuranceConnection, null);

        if (byCrmCode.TryGetValue(crmCode, out var mapped))
            return (ProductRoutability.Mapped, mapped);

        // The fallback names its connection too: the point of the report is to say where a
        // subscription GOES, and "it works but I cannot tell you where" would send an
        // administrator back to the mappings screen to find out.
        return connectionIds.Count == 1
            ? (ProductRoutability.SingleConnectionFallback, connectionIds[0])
            : (ProductRoutability.Unroutable, null);
    }
}
