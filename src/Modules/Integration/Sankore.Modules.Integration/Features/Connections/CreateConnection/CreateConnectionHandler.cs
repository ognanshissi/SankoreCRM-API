namespace Sankore.Modules.Integration.Features.Connections.CreateConnection;

using MediatR;
using Microsoft.Extensions.Logging;
using Sankore.Modules.Integration.Domain;
using Sankore.Modules.Integration.Infrastructure;
using Sankore.Modules.Integration.PublicApi;
using Sankore.Shared.Infrastructure.Auth;
using Sankore.Shared.Kernel;

internal sealed class CreateConnectionHandler(
    IntegrationDbContext db,
    ICurrentUser currentUser,
    TimeProvider clock,
    ILogger<CreateConnectionHandler> logger)
    : IRequestHandler<CreateConnectionCommand, Result<Guid>>
{
    public async Task<Result<Guid>> Handle(CreateConnectionCommand cmd, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(cmd);

        // The validator already refuses both cases. Repeated here because this handler is also
        // reachable from a test and from a future seeder, and the aggregate answers a mismatch
        // with a DomainException — a 500 where the caller deserves an error code.
        if (cmd.Settings is null || cmd.Settings.ExpectedKind != cmd.Kind)
            return Result.Fail<Guid>(IntegrationErrors.SettingsInvalid);

        var connection = IntegrationConnection.Create(
            tenantId: currentUser.TenantId,
            family: cmd.Family,
            kind: cmd.Kind,
            mode: cmd.Mode,
            name: cmd.Name,
            settings: cmd.Settings,
            createdBy: currentUser.Id,
            clock: clock,
            // Never from the request. See CreateConnectionCommand: a client-settable relay agent
            // id is a cross-tenant leak, and INT-27's enrolment flow is what sets this.
            relayAgentId: null);

        db.Connections.Add(connection);
        await db.SaveChangesAsync(ct);

        // Named, never dumped: a settings object is small enough to be tempting to log and is
        // exactly where an operator's copy-pasted credential would end up if one ever slipped
        // into a coordinate field.
        logger.LogInformation(
            "Integration connection {ConnectionId} ({Family}/{Kind}) created for tenant {TenantId}",
            connection.Id, cmd.Family, cmd.Kind, currentUser.TenantId);

        return Result.Ok(connection.Id);
    }
}
