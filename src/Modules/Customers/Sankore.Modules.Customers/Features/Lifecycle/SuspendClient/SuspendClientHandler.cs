namespace Sankore.Modules.Customers.Features.Lifecycle.SuspendClient;

using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Sankore.Modules.Customers.Domain;
using Sankore.Modules.Customers.Infrastructure;
using Sankore.Modules.Customers.PublicApi.Events;
using Sankore.Shared.Infrastructure.Auth;
using Sankore.Shared.Infrastructure.Messaging;
using Sankore.Shared.Kernel;
using Sankore.Shared.Kernel.Authorization;

internal sealed class SuspendClientHandler(
    CustomersDbContext db,
    ICurrentUser currentUser,
    IAgencyScopeProvider agencyScope,
    [FromKeyedServices(nameof(CustomersDbContext))] IEventPublisher publisher)
    : IRequestHandler<SuspendClientCommand, Result<ClientLifecycleStateDto>>
{
    public async Task<Result<ClientLifecycleStateDto>> Handle(
        SuspendClientCommand cmd, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(cmd.Reason))
            return Result.Fail<ClientLifecycleStateDto>(CustomerErrors.ReasonRequired);

        // AsTracking: the context is NoTracking by default and we are about to mutate.
        var client = await db.Clients
            .AsTracking()
            .FirstOrDefaultAsync(c => c.Id == cmd.ClientId, ct);

        if (client is null)
            return Result.Fail<ClientLifecycleStateDto>(CustomerErrors.ClientNotFound);

        // The agency is not part of the command, so AgencyAuthorizationBehavior
        // cannot vet it: check here. Out of perimeter answers NOT_FOUND, never
        // OUT_OF_SCOPE — the perimeter must not reveal that the record exists.
        if (!await agencyScope.CanAccessAgencyAsync(
                currentUser.TenantId, currentUser.Id, client.AgencyId, ct))
            return Result.Fail<ClientLifecycleStateDto>(CustomerErrors.ClientNotFound);

        // The domain owns the transition table: an illegal source status returns
        // INVALID_STATUS_TRANSITION, a read-only record CLIENT_READ_ONLY, and the
        // status-history line is appended by the aggregate itself.
        var transition = client.Suspend(cmd.Reason.Trim(), currentUser.Id);
        if (transition.IsFailure)
            return Result.Fail<ClientLifecycleStateDto>(transition.Error!);

        // Outbox write BEFORE SaveChanges: publisher only stages the row, the single
        // SaveChanges below commits the status change and the event atomically.
        await publisher.PublishAsync(
            new ClientSuspendedEvent(client.TenantId, client.Id, cmd.Reason.Trim(), currentUser.Id), ct);

        await db.SaveChangesAsync(ct);

        return Result.Ok(client.ToLifecycleState());
    }
}
