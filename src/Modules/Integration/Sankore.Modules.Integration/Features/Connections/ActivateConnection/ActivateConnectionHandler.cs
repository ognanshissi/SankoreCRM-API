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
    private const string ActiveTelephonyIndex = "ux_integration_connection_active_telephony";
    private const string ActiveChannelAddressIndex = "ux_integration_connection_active_channel_address";

    /// <summary>
    /// The families of which a tenant may hold at most one active connection, and the code each
    /// answers with. Insurance is deliberately absent: an IMF distributes for several insurers.
    /// </summary>
    private static readonly IReadOnlyDictionary<IntegrationFamily, string> ExclusiveFamilies =
        new Dictionary<IntegrationFamily, string>
        {
            [IntegrationFamily.CoreBanking] = IntegrationErrors.CoreBankingConnectionAlreadyActive,
            [IntegrationFamily.Telephony] = IntegrationErrors.TelephonyConnectionAlreadyActive,
        };

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
        if (ExclusiveFamilies.TryGetValue(connection.Family, out var alreadyActiveError))
        {
            var family = connection.Family;

            var anotherIsActive = await db.Connections
                .AnyAsync(
                    c => c.Family == family
                      && c.IsActive
                      && c.Id != connection.Id,
                    ct);

            if (anotherIsActive)
                return Result.Fail(alreadyActiveError);
        }

        // A WhatsApp number answers to one institution at a time (WA-BE-02), and the question is
        // asked ACROSS tenants — hence IgnoreQueryFilters. Same pairing as above: this read is the
        // friendly answer, the global index is the guarantee.
        if (connection.ChannelAddress is { } address)
        {
            var claimed = await db.Connections
                .IgnoreQueryFilters()
                .AnyAsync(
                    c => c.ChannelAddress == address
                      && c.IsActive
                      && c.Id != connection.Id,
                    ct);

            if (claimed)
                return Result.Fail(IntegrationErrors.MessagingNumberAlreadyConnected);
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
        catch (DbUpdateException ex) when (ViolatedExclusiveIndex(ex) is not null)
        {
            logger.LogWarning(
                "Lost the race to activate {Family} connection {ConnectionId} for tenant {TenantId}",
                connection.Family, connection.Id, currentUser.TenantId);

            return Result.Fail(ViolatedExclusiveIndex(ex)!);
        }

        logger.LogInformation(
            "Integration connection {ConnectionId} ({Family}/{Kind}) activated for tenant {TenantId}",
            connection.Id, connection.Family, connection.Kind, currentUser.TenantId);

        return Result.Ok();
    }

    /// <summary>
    /// The error code of the exclusivity index this violation names, or null when the failure is
    /// something else. Matched BY INDEX NAME, as before: a bare unique-violation check would
    /// swallow any other constraint this table grows.
    /// </summary>
    private static string? ViolatedExclusiveIndex(DbUpdateException ex)
    {
        if (Names(ex, ActiveCoreBankingIndex)) return IntegrationErrors.CoreBankingConnectionAlreadyActive;
        if (Names(ex, ActiveTelephonyIndex)) return IntegrationErrors.TelephonyConnectionAlreadyActive;
        if (Names(ex, ActiveChannelAddressIndex)) return IntegrationErrors.MessagingNumberAlreadyConnected;

        return null;

        static bool Names(DbUpdateException ex, string index)
            => ex.InnerException?.Message.Contains(index, StringComparison.OrdinalIgnoreCase) == true
               || ex.Message.Contains(index, StringComparison.OrdinalIgnoreCase);
    }
}
