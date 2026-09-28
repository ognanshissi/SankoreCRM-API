namespace Sankore.Modules.Customers.Features.Lifecycle.ArchiveClient;

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

internal sealed class ArchiveClientHandler(
    CustomersDbContext db,
    ICurrentUser currentUser,
    IAgencyScopeProvider agencyScope,
    IOutstandingBalanceProbe outstandingBalances,
    [FromKeyedServices(nameof(CustomersDbContext))] IEventPublisher publisher)
    : IRequestHandler<ArchiveClientCommand, Result<ClientLifecycleStateDto>>
{
    /// <summary>
    /// Not yet a constant of <c>CustomerErrors</c> (that file belongs to the socle
    /// and is written in parallel): the literal is the contract for now and will be
    /// consolidated into <c>CustomerErrors.ClientHasActiveCommitments</c> at
    /// integration time. Keep the exact wire value.
    /// </summary>
    private const string ClientHasActiveCommitments = "CLIENT_HAS_ACTIVE_COMMITMENTS";

    public async Task<Result<ClientLifecycleStateDto>> Handle(
        ArchiveClientCommand cmd, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(cmd.Reason))
            return Result.Fail<ClientLifecycleStateDto>(CustomerErrors.ReasonRequired);

        var client = await db.Clients
            .AsTracking()
            .FirstOrDefaultAsync(c => c.Id == cmd.ClientId, ct);

        if (client is null)
            return Result.Fail<ClientLifecycleStateDto>(CustomerErrors.ClientNotFound);

        // Out of perimeter answers NOT_FOUND, never OUT_OF_SCOPE (no existence leak).
        if (!await agencyScope.CanAccessAgencyAsync(
                currentUser.TenantId, currentUser.Id, client.AgencyId, ct))
            return Result.Fail<ClientLifecycleStateDto>(CustomerErrors.ClientNotFound);

        // Archiving is the point of no return for commercial activity, so it is
        // asked BEFORE the transition, not after: a client who still owes an
        // instalment or holds a blocked savings account cannot be archived.
        // Today NoOutstandingBalanceProbe answers false (M03/M04 not deployed);
        // the rule activates the day a real probe is registered, with no change here.
        if (await outstandingBalances.HasActiveCommitmentsAsync(client.TenantId, client.Id, ct))
            return Result.Fail<ClientLifecycleStateDto>(ClientHasActiveCommitments);

        var transition = client.Archive(cmd.Reason.Trim(), currentUser.Id);
        if (transition.IsFailure)
            return Result.Fail<ClientLifecycleStateDto>(transition.Error!);

        await publisher.PublishAsync(
            new ClientArchivedEvent(
                client.TenantId,
                client.Id,
                cmd.Reason.Trim(),
                currentUser.Id,
                client.ArchivedAt ?? DateTimeOffset.UtcNow),
            ct);

        await db.SaveChangesAsync(ct);

        return Result.Ok(client.ToLifecycleState());
    }
}
