namespace Sankore.Modules.Customers.Features.Lifecycle.ReactivateClient;

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

internal sealed class ReactivateClientHandler(
    CustomersDbContext db,
    ICurrentUser currentUser,
    IAgencyScopeProvider agencyScope,
    [FromKeyedServices(nameof(CustomersDbContext))] IEventPublisher publisher)
    : IRequestHandler<ReactivateClientCommand, Result<ClientLifecycleStateDto>>
{
    public async Task<Result<ClientLifecycleStateDto>> Handle(
        ReactivateClientCommand cmd, CancellationToken ct)
    {
        var client = await db.Clients
            .AsTracking()
            .FirstOrDefaultAsync(c => c.Id == cmd.ClientId, ct);

        if (client is null)
            return Result.Fail<ClientLifecycleStateDto>(CustomerErrors.ClientNotFound);

        // Out of perimeter answers NOT_FOUND, never OUT_OF_SCOPE (no existence leak).
        if (!await agencyScope.CanAccessAgencyAsync(
                currentUser.TenantId, currentUser.Id, client.AgencyId, ct))
            return Result.Fail<ClientLifecycleStateDto>(CustomerErrors.ClientNotFound);

        var statusBefore = client.Status;

        var transition = client.Reactivate(currentUser.Id);
        if (transition.IsFailure)
            return Result.Fail<ClientLifecycleStateDto>(transition.Error!);

        // ClientActivatedEvent is emitted only on the Active landing: a client that
        // goes back to PendingKyc is NOT active and must not wake up the consumers
        // that react to activation.
        if (statusBefore != ClientStatus.Active && client.Status == ClientStatus.Active)
            await publisher.PublishAsync(new ClientActivatedEvent(client.TenantId, client.Id), ct);

        await db.SaveChangesAsync(ct);

        return Result.Ok(client.ToLifecycleState());
    }
}
