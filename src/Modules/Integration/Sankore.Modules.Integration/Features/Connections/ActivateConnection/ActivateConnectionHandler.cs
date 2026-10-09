namespace Sankore.Modules.Integration.Features.Connections.ActivateConnection;

using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Sankore.Modules.Integration.Infrastructure;
using Sankore.Modules.Integration.PublicApi;
using Sankore.Shared.Infrastructure.Auth;
using Sankore.Shared.Kernel;

internal sealed class ActivateConnectionHandler(
    IntegrationDbContext db,
    ICurrentUser currentUser,
    TimeProvider clock,
    ILogger<ActivateConnectionHandler> logger)
    : IRequestHandler<ActivateConnectionCommand, Result>
{
    /// <summary>
    /// The partial unique index of <c>integration_connection</c>. Matched by NAME so that only
    /// THIS constraint is swallowed: any other violation is a bug and must keep propagating
    /// instead of being reported as "a core banking connection is already active".
    /// </summary>
    private const string ActiveCoreBankingIndex = "ux_integration_connection_active_core_banking";

    public async Task<Result> Handle(ActivateConnectionCommand cmd, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(cmd);

        // AsTracking: this mutates. No tenant predicate — the global query filter scopes it, so
        // another tenant's connection answers 404 and never 403.
        var connection = await db.Connections
            .AsTracking()
            .FirstOrDefaultAsync(c => c.Id == cmd.ConnectionId, ct);

        if (connection is null)
            return Result.Fail(IntegrationErrors.ConnectionNotFound);

        // Already active: the caller's intent is satisfied, so this is a success and not a
        // conflict. Reporting 409 here would make a double-click on the activation button look
        // like the "one active core banking connection" rule firing.
        if (connection.IsActive)
            return Result.Ok();

        // ONE active core banking connection per tenant, SEVERAL insurance ones: a customer
        // cannot be created in two core banking systems, while an IMF legitimately distributes
        // for several insurers (ASS-01).
        //
        // This read is the friendly answer, not the guarantee. Two concurrent activations would
        // both pass it, which is why the partial unique index exists and why its violation is
        // caught below. Both are needed: the index alone would answer a bare 500 in the ordinary
        // case, and the read alone would let a race through.
        if (connection.Family == IntegrationFamily.CoreBanking)
        {
            var anotherIsActive = await db.Connections
                .AnyAsync(
                    c => c.Family == IntegrationFamily.CoreBanking
                      && c.IsActive
                      && c.Id != connection.Id,
                    ct);

            if (anotherIsActive)
                return Result.Fail(IntegrationErrors.CoreBankingConnectionAlreadyActive);
        }

        // Activation requires a passed health check. The aggregate owns the rule; the handler
        // only relays its refusal.
        var activation = connection.Activate(currentUser.Id, clock);

        if (activation.IsFailure)
            return activation;

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            // Caught before the DbUpdateException below: it derives from it.
            return Result.Fail(IntegrationErrors.ConcurrencyConflict);
        }
        catch (DbUpdateException ex) when (IsActiveCoreBankingViolation(ex))
        {
            logger.LogWarning(
                "Lost the race to activate core banking connection {ConnectionId} for tenant {TenantId}",
                connection.Id, currentUser.TenantId);

            return Result.Fail(IntegrationErrors.CoreBankingConnectionAlreadyActive);
        }

        logger.LogInformation(
            "Integration connection {ConnectionId} ({Family}/{Kind}) activated for tenant {TenantId}",
            connection.Id, connection.Family, connection.Kind, currentUser.TenantId);

        return Result.Ok();
    }

    private static bool IsActiveCoreBankingViolation(DbUpdateException ex)
        => ex.InnerException?.Message.Contains(ActiveCoreBankingIndex, StringComparison.OrdinalIgnoreCase) == true
           || ex.Message.Contains(ActiveCoreBankingIndex, StringComparison.OrdinalIgnoreCase);
}
