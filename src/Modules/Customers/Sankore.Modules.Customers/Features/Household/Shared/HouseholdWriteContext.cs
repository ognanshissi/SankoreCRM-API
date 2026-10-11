namespace Sankore.Modules.Customers.Features.Household.Shared;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Sankore.Modules.Administration.PublicApi;
using Sankore.Modules.Customers.Domain;
using Sankore.Modules.Customers.Infrastructure;
using Sankore.Modules.Customers.PublicApi.Events;
using Sankore.Shared.Infrastructure.Auth;
using Sankore.Shared.Infrastructure.Messaging;
using Sankore.Shared.Kernel;
using Sankore.Shared.Kernel.Authorization;

/// <summary>
/// The client and the household rows a household write needs, with every gate already run.
/// </summary>
/// <param name="PepPostMandateMonths">
/// The tenant's window, from M12's compliance settings. Needed on EVERY write and not only the PEP
/// ones: adding or removing a member can flip <c>Client.HasPepExposure</c>, and deciding that needs
/// the window.
/// </param>
internal sealed record HouseholdWriteScope(
    Client Client,
    List<ClientRelationship> Household,
    int PepPostMandateMonths);

/// <summary>
/// The gates every household write of M01 runs, in the one order they have to happen (BNF-B-13).
///
/// <para>
/// Modelled on M13's <c>HouseholdWriteContext</c> — load, perimeter, window, then commit with the
/// exposure recalculation — but it is NOT the same class and cannot be: the perimeter here is M01's
/// agency scope (a read outside it answers <c>CLIENT_NOT_FOUND</c> and never 403, so existence does
/// not leak), and what it loads is a client plus a filtered slice of a shared relationship table.
/// </para>
/// </summary>
internal sealed class HouseholdWriteContext(
    CustomersDbContext db,
    ICurrentUser currentUser,
    IAgencyScopeProvider agencyScope,
    IAdministrationModule administration,
    [FromKeyedServices(nameof(CustomersDbContext))] IEventPublisher publisher,
    TimeProvider clock,
    ILogger<HouseholdWriteContext> logger)
{
    internal TimeProvider Clock => clock;

    /// <summary>
    /// Loads the client and its ACTIVE household, after the perimeter and the read-only checks.
    /// </summary>
    /// <remarks>
    /// The household is loaded in full even for a write that touches one member, because the
    /// invariants are about the set: "at most one active spouse" is a count over these rows, and a
    /// handler that loaded only its own member would add a second spouse and discover it as a raw
    /// <c>23505</c> from <c>uq_client_one_spouse</c>.
    /// </remarks>
    internal async Task<Result<HouseholdWriteScope>> LoadAsync(Guid clientId, CancellationToken ct)
    {
        var client = await db.Clients
            .AsTracking()
            .FirstOrDefaultAsync(c => c.Id == clientId, ct);

        // Out of perimeter reads as missing — the module's existing rule, so a 404 cannot be used
        // to probe for a client's existence.
        if (client is null ||
            !await agencyScope.CanAccessAgencyAsync(currentUser.TenantId, currentUser.Id, client.AgencyId, ct))
            return Result.Fail<HouseholdWriteScope>(CustomerErrors.ClientNotFound);

        if (client.IsReadOnly)
            return Result.Fail<HouseholdWriteScope>(CustomerErrors.ClientReadOnly);

        var household = await db.ClientRelationships
            .AsTracking()
            .Where(r => r.ClientId == clientId
                     && r.ValidTo == null
                     && (r.Type == RelationshipType.Spouse
                      || r.Type == RelationshipType.Child
                      || r.Type == RelationshipType.Dependent))
            .ToListAsync(ct);

        var window = await PepWindowAsync(client.TenantId, ct);
        if (window.IsFailure)
            return Result.Fail<HouseholdWriteScope>(window.Error!);

        return Result.Ok(new HouseholdWriteScope(client, household, window.Value));
    }

    /// <summary>
    /// The tenant's PEP post-mandate window (BNF-B-13).
    /// </summary>
    /// <remarks>
    /// FAILS CLOSED on an outage rather than falling back to the platform default of 12 months — the
    /// same posture M13 takes, for the same reason: a tenant that configured 24 would otherwise have
    /// members silently read as no longer exposed, and an LBC/FT flag computed from a guessed
    /// parameter is worse than a write the agent repeats.
    /// </remarks>
    internal async Task<Result<int>> PepWindowAsync(Guid tenantId, CancellationToken ct)
    {
        try
        {
            using var suppressed = new System.Transactions.TransactionScope(
                System.Transactions.TransactionScopeOption.Suppress,
                System.Transactions.TransactionScopeAsyncFlowOption.Enabled);

            var settings = await administration.GetComplianceSettingsAsync(tenantId, ct);
            suppressed.Complete();

            return Result.Ok(settings.PepPostMandateMonths);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Same reason as M13's: the caller gets a bare error code, and every cause of this
            // failure lives in another module.
            logger.LogError(
                ex,
                "Compliance settings unavailable for tenant {TenantId}; the PEP post-mandate "
                + "window could not be resolved, so the household write was refused.",
                tenantId);

            return Result.Fail<int>(CustomerErrors.ComplianceSettingsUnavailable);
        }
    }

    /// <summary>
    /// Recomputes the client's exposure over its household, publishes the KYC review request when
    /// the flag ROSE, and saves — all in one transaction.
    /// </summary>
    /// <remarks>
    /// The recalculation is here and not in each handler because every household write can move the
    /// flag: a spouse added, a child removed, a status declared. A handler that forgot would leave
    /// a client reading "not exposed" with an exposed spouse on file, which is the one state an
    /// LBC/FT review must never see.
    /// </remarks>
    internal async Task CommitAsync(HouseholdWriteScope scope, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var asOf = DateOnly.FromDateTime(now.UtcDateTime);

        // Over the household as it stands AFTER the write, which is why this reads the tracked
        // list rather than re-querying: a member removed in this transaction is still a row in the
        // database and would count.
        var householdExposed = scope.Household
            .Any(m => m.IsActive && m.IsPepExposedAt(asOf, scope.PepPostMandateMonths));

        var rose = scope.Client.RecalculateHasPepExposure(
            householdExposed, asOf, scope.PepPostMandateMonths, now);

        if (rose)
        {
            // Through the outbox, in the client's own transaction: a review requested for a flag
            // that was never stored, or a flag stored with no review, are both worse than neither.
            await publisher.PublishAsync(
                new ClientPepExposureRaisedEvent(
                    scope.Client.TenantId, scope.Client.Id, currentUser.Id),
                ct);
        }

        await db.SaveChangesAsync(ct);
    }
}
