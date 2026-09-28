namespace Sankore.Modules.Customers.Features.Compliance.Retention.AnonymizeClient;

using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Sankore.Modules.Customers.Domain;
using Sankore.Modules.Customers.PublicApi.Events;
using Sankore.Modules.Customers.Infrastructure;
using Sankore.Modules.Kyc.PublicApi;
using Sankore.Shared.Infrastructure.Auth;
using Sankore.Shared.Infrastructure.Messaging;
using Sankore.Shared.Kernel;
using Sankore.Shared.Kernel.Authorization;

/// <summary>
/// Executes the irreversible erasure. Three independent gates must all open first — the archive
/// must be past the retention term, the KYC module must have released the file, and the caller
/// must be inside the client's agency perimeter — because nothing can be undone afterwards.
/// </summary>
internal sealed class AnonymizeClientHandler(
    CustomersDbContext db,
    ICurrentUser currentUser,
    IAgencyScopeProvider agencyScope,
    ICustomerSettings settings,
    IKycModule kyc,
    [FromKeyedServices(nameof(CustomersDbContext))] IEventPublisher publisher,
    TimeProvider clock,
    ILogger<AnonymizeClientHandler> logger
) : IRequestHandler<AnonymizeClientCommand, Result>
{
    public async Task<Result> Handle(AnonymizeClientCommand command, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(command.Reason))
            return Result.Fail(CustomerErrors.ReasonRequired);

        var tenantId = currentUser.TenantId;

        // AsTracking + the contact points: Client.Anonymize() closes every active contact point,
        // so they have to be loaded or the erasure would leave them behind.
        var client = await db.Clients
            .AsTracking()
            .Include(c => c.ContactPoints)
            .FirstOrDefaultAsync(c => c.Id == command.ClientId, ct);

        if (client is null)
            return Result.Fail(CustomerErrors.ClientNotFound);

        // Guid.Empty is the SYSTEM account used by the monthly job: it has no agency of its own,
        // and asking the scope provider about it would return an empty set (unknown user) and
        // deny the job. Everyone else goes through the perimeter check, which answers
        // CLIENT_NOT_FOUND rather than 403 so the response never confirms the client exists.
        var isSystemActor = currentUser.Id == Guid.Empty;
        if (!isSystemActor)
        {
            var allowed = await agencyScope.CanAccessAgencyAsync(
                tenantId, currentUser.Id, client.AgencyId, ct);

            if (!allowed)
                return Result.Fail(CustomerErrors.ClientNotFound);
        }

        // Idempotent: a double click, or a retried Hangfire attempt, must not republish the event.
        if (client.IsAnonymized)
            return Result.Ok();

        var now = clock.GetUtcNow();
        var retentionYears = await RetentionWindow.ResolveAsync(settings, tenantId, ct);
        var cutOff = RetentionWindow.CutOff(now, retentionYears);

        // Only an archived client can be erased, and only once the term has fully elapsed.
        if (client.Status != ClientStatus.Archived
            || client.ArchivedAt is null
            || client.ArchivedAt >= cutOff)
        {
            return Result.Fail(CustomerErrors.RetentionNotReached);
        }

        var cleared = await kyc.IsRetentionClearedAsync(tenantId, client.Id, ct);
        if (!cleared)
            return Result.Fail(CustomerErrors.KycRetentionNotCleared);

        client.Anonymize(currentUser.Id, now);

        // Publish inside the same transaction as the erasure (the outbox writer does not save):
        // either both land or neither does, so no consumer can ever see an event for data that
        // is in fact still there.
        await publisher.PublishAsync(new ClientAnonymizedEvent(tenantId, client.Id, now), ct);
        await db.SaveChangesAsync(ct);

        logger.LogWarning(
            "Client {ClientId} ({ClientNumber}) of tenant {TenantId} anonymized by {ActorId} " +
            "after {RetentionYears} years of archive. Reason: {Reason}",
            client.Id, client.ClientNumber, tenantId,
            isSystemActor ? "SYSTEM" : currentUser.Id.ToString(),
            retentionYears, command.Reason);

        return Result.Ok();
    }
}
