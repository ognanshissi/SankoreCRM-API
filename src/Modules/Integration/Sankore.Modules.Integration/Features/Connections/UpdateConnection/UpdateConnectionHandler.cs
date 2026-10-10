namespace Sankore.Modules.Integration.Features.Connections.UpdateConnection;

using MediatR;
using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Integration.Infrastructure;
using Sankore.Modules.Integration.PublicApi;
using Sankore.Shared.Infrastructure.Auth;
using Sankore.Shared.Kernel;

internal sealed class UpdateConnectionHandler(
    IntegrationDbContext db,
    ICurrentUser currentUser,
    TimeProvider clock)
    : IRequestHandler<UpdateConnectionCommand, Result>
{
    public async Task<Result> Handle(UpdateConnectionCommand cmd, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(cmd);

        if (cmd.Settings is null)
            return Result.Fail(IntegrationErrors.SettingsInvalid);

        // AsTracking: the context is NoTracking by default and this path mutates.
        //
        // No tenant predicate: IntegrationDbContext's global query filter already scopes this to
        // the caller's tenant. A connection belonging to ANOTHER tenant is therefore
        // indistinguishable from one that does not exist, and both answer 404 — never 403. A 403
        // would confirm that this id names a real connection somewhere on the platform, which is
        // itself information the caller is not entitled to.
        var connection = await db.Connections
            .AsTracking()
            .FirstOrDefaultAsync(c => c.Id == cmd.ConnectionId, ct);

        if (connection is null)
            return Result.Fail(IntegrationErrors.ConnectionNotFound);

        if (cmd.ExpectedVersion is not null && connection.Version != cmd.ExpectedVersion)
            return Result.Fail(IntegrationErrors.ConcurrencyConflict);

        if (cmd.ExpectedUpdatedAt is not null && connection.UpdatedAt != cmd.ExpectedUpdatedAt)
            return Result.Fail(IntegrationErrors.ConcurrencyConflict);

        // The settings must belong to the stored row's kind. The validator cannot check this —
        // the kind is in the database — and the aggregate answers with a DomainException, so the
        // guard lives here to turn it into an error code.
        if (cmd.Settings.ExpectedKind != connection.Kind)
            return Result.Fail(IntegrationErrors.SettingsInvalid);

        connection.UpdateSettings(
            name: cmd.Name,
            mode: cmd.Mode,
            settings: cmd.Settings,
            // The STORED value, never one from the request: a relay agent id taken from a body
            // could name another tenant's on-premise agent, which would execute this tenant's
            // payloads inside that tenant's network. Carried through rather than passed as null
            // so an existing link survives an ordinary settings edit. INT-27 owns this field.
            relayAgentId: connection.RelayAgentId,
            updatedBy: currentUser.Id,
            clock: clock);

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            // Someone committed between our read and our write: the row's xmin moved.
            return Result.Fail(IntegrationErrors.ConcurrencyConflict);
        }

        return Result.Ok();
    }
}
