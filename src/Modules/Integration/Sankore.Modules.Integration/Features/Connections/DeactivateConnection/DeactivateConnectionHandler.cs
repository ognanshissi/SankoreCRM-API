namespace Sankore.Modules.Integration.Features.Connections.DeactivateConnection;

using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Sankore.Modules.Integration.Infrastructure;
using Sankore.Modules.Integration.PublicApi;
using Sankore.Shared.Infrastructure.Auth;
using Sankore.Shared.Kernel;

internal sealed class DeactivateConnectionHandler(
    IntegrationDbContext db,
    ICurrentUser currentUser,
    TimeProvider clock,
    ILogger<DeactivateConnectionHandler> logger)
    : IRequestHandler<DeactivateConnectionCommand, Result>
{
    public async Task<Result> Handle(DeactivateConnectionCommand cmd, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(cmd);

        // AsTracking: this mutates. No tenant predicate — the global query filter scopes it, so
        // another tenant's connection answers 404 and never 403.
        var connection = await db.Connections
            .AsTracking()
            .FirstOrDefaultAsync(c => c.Id == cmd.ConnectionId, ct);

        if (connection is null)
            return Result.Fail(IntegrationErrors.ConnectionNotFound);

        // Already inactive: the caller's intent is satisfied.
        if (!connection.IsActive)
            return Result.Ok();

        connection.Deactivate(currentUser.Id, clock);

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            return Result.Fail(IntegrationErrors.ConcurrencyConflict);
        }

        // Worth an INFORMATION line of its own: from here on, every command of this tenant for
        // that family answers INTEGRATION_NO_ACTIVE_CONNECTION, and the first question asked when
        // that happens is when the connection was switched off, and by whom.
        logger.LogInformation(
            "Integration connection {ConnectionId} ({Family}/{Kind}) deactivated for tenant {TenantId}",
            connection.Id, connection.Family, connection.Kind, currentUser.TenantId);

        return Result.Ok();
    }
}
