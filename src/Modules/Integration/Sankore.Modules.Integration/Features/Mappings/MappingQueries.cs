namespace Sankore.Modules.Integration.Features.Mappings;

using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Integration.Infrastructure;

internal static class MappingQueries
{
    /// <summary>
    /// Whether the connection exists FOR THE CALLER'S TENANT. No <c>IgnoreQueryFilters</c>: the
    /// context's tenant filter is exactly the guarantee wanted here, so a connection belonging to
    /// another tenant answers false and every slice turns that into 404 — never 403. A 403 would
    /// confirm that the id exists somewhere, which is enough to enumerate other tenants'
    /// connections.
    /// </summary>
    internal static Task<bool> ConnectionExistsAsync(
        this IntegrationDbContext db, Guid connectionId, CancellationToken ct)
        => db.Connections.AnyAsync(c => c.Id == connectionId, ct);
}
