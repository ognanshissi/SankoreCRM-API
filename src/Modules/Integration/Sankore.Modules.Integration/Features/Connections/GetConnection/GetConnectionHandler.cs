namespace Sankore.Modules.Integration.Features.Connections.GetConnection;

using MediatR;
using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Integration.Infrastructure;
using Sankore.Modules.Integration.PublicApi;
using Sankore.Shared.Kernel;

internal sealed class GetConnectionHandler(IntegrationDbContext db)
    : IRequestHandler<GetConnectionQuery, Result<ConnectionDetailDto>>
{
    public async Task<Result<ConnectionDetailDto>> Handle(
        GetConnectionQuery query, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(query);

        // No tenant predicate: the DbContext's global query filter scopes the read. A connection
        // of ANOTHER tenant is therefore indistinguishable from one that does not exist, and both
        // answer 404 — never 403. The existence of a connection must not leak: it would tell a
        // caller which institutions share this platform, and which core banking system they run.
        var connection = await db.Connections
            .FirstOrDefaultAsync(c => c.Id == query.ConnectionId, ct);

        return connection is null
            ? Result.Fail<ConnectionDetailDto>(IntegrationErrors.ConnectionNotFound)
            : Result.Ok(ConnectionDetailDto.From(connection));
    }
}
