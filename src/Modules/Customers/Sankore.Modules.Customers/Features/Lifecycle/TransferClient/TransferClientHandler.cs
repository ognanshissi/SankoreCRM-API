namespace Sankore.Modules.Customers.Features.Lifecycle.TransferClient;

using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Sankore.Modules.Customers.Domain;
using Sankore.Modules.Customers.Features.Clients.Shared;
using Sankore.Modules.Customers.Infrastructure;
using Sankore.Modules.Customers.PublicApi.Events;
using Sankore.Shared.Infrastructure.Auth;
using Sankore.Shared.Infrastructure.Messaging;
using Sankore.Shared.Kernel;
using Sankore.Shared.Kernel.Authorization;

internal sealed class TransferClientHandler(
    CustomersDbContext db,
    ICurrentUser currentUser,
    IAgencyScopeProvider agencyScope,
    IAgencyDirectory agencyDirectory,
    [FromKeyedServices(nameof(CustomersDbContext))] IEventPublisher publisher)
    : IRequestHandler<TransferClientCommand, Result<ClientLifecycleStateDto>>
{
    public async Task<Result<ClientLifecycleStateDto>> Handle(
        TransferClientCommand cmd, CancellationToken ct)
    {
        // AgencyAuthorizationBehavior has already vetted the DESTINATION agency
        // (IAgencyScopedRequest.TargetAgencyId) and answered AGENCY_OUT_OF_SCOPE if
        // needed; everything below concerns the source record.
        if (string.IsNullOrWhiteSpace(cmd.Reason))
            return Result.Fail<ClientLifecycleStateDto>(CustomerErrors.ReasonRequired);

        var client = await db.Clients
            .AsTracking()
            .FirstOrDefaultAsync(c => c.Id == cmd.ClientId, ct);

        if (client is null)
            return Result.Fail<ClientLifecycleStateDto>(CustomerErrors.ClientNotFound);

        // Source side of the perimeter: you may only move a client you can already
        // see. Out of perimeter answers NOT_FOUND, never OUT_OF_SCOPE.
        if (!await agencyScope.CanAccessAgencyAsync(
                currentUser.TenantId, currentUser.Id, client.AgencyId, ct))
            return Result.Fail<ClientLifecycleStateDto>(CustomerErrors.ClientNotFound);

        // Idempotent no-op: re-posting the same destination must not append a
        // history line nor publish a A -> A transfer event.
        if (client.AgencyId == cmd.TargetAgencyId)
            return Result.Ok(client.ToLifecycleState());

        // The agency code is denormalized on the client (it feeds the client number
        // and every export), so it has to be resolved from Administration. An unknown
        // agency is reported as out of scope, exactly like in the creation slice:
        // the caller learns nothing about agencies it may not address.
        var targetAgencyCode = await agencyDirectory.GetAgencyCodeAsync(
            client.TenantId, cmd.TargetAgencyId, ct);

        if (string.IsNullOrWhiteSpace(targetAgencyCode))
            return Result.Fail<ClientLifecycleStateDto>(CustomerErrors.AgencyOutOfScope);

        // The advisor follows the client only if they also belong to the destination
        // agency; otherwise the assignment is reset so the receiving branch is forced
        // to appoint one of its own officers.
        var keepAdvisor = client.AdvisorUserId is { } advisorUserId
            && await agencyDirectory.IsAdvisorEligibleAsync(
                client.TenantId, advisorUserId, cmd.TargetAgencyId, ct);

        var fromAgencyId = client.AgencyId;

        var transition = client.TransferToAgency(
            cmd.TargetAgencyId, targetAgencyCode!, keepAdvisor, currentUser.Id);

        if (transition.IsFailure)
            return Result.Fail<ClientLifecycleStateDto>(transition.Error!);

        await publisher.PublishAsync(
            new ClientTransferredEvent(
                client.TenantId,
                client.Id,
                fromAgencyId,
                cmd.TargetAgencyId,
                client.AdvisorUserId,
                currentUser.Id),
            ct);

        await db.SaveChangesAsync(ct);

        return Result.Ok(client.ToLifecycleState());
    }
}
