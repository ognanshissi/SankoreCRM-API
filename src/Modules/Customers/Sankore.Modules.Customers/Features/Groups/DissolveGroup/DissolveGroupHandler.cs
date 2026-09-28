namespace Sankore.Modules.Customers.Features.Groups.DissolveGroup;

using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Sankore.Modules.Customers.Domain;
using Sankore.Modules.Customers.Features.Groups.Shared;
using Sankore.Modules.Customers.Infrastructure;
using Sankore.Modules.Customers.PublicApi.Events;
using Sankore.Shared.Infrastructure.Auth;
using Sankore.Shared.Infrastructure.Messaging;
using Sankore.Shared.Kernel;
using Sankore.Shared.Kernel.Authorization;

internal sealed class DissolveGroupHandler(
    CustomersDbContext db,
    ICurrentUser currentUser,
    IAgencyScopeProvider agencyScope,
    [FromKeyedServices(nameof(CustomersDbContext))] IEventPublisher publisher
) : IRequestHandler<DissolveGroupCommand, Result<DissolveGroupResult>>
{
    public async Task<Result<DissolveGroupResult>> Handle(DissolveGroupCommand request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Reason))
            return Result.Fail<DissolveGroupResult>(CustomerErrors.ReasonRequired);

        var group = await db.ClientGroups
            .AsTracking()
            .Include(g => g.Memberships)
            .FirstOrDefaultAsync(g => g.Id == request.GroupId, ct);

        if (group is null)
            return Result.Fail<DissolveGroupResult>(CustomerErrors.GroupNotFound);

        if (!await GroupAccess.CanAccessAsync(agencyScope, currentUser.TenantId, currentUser.Id, group, ct))
            return Result.Fail<DissolveGroupResult>(CustomerErrors.GroupNotFound);

        if (request.ExpectedVersion.HasValue && group.Version != request.ExpectedVersion.Value)
            return Result.Fail<DissolveGroupResult>(CustomerErrors.ConcurrencyConflict);

        // Counted before the aggregate closes them; afterwards ActiveMemberCount is 0.
        var openMemberships = group.ActiveMemberCount;
        var reason = request.Reason.Trim();

        // The cascade (close every open membership) is an aggregate invariant of
        // ClientGroup.Dissolve, not handler logic: no caller can end up with a
        // dissolved group that still has live members.
        var dissolution = group.Dissolve(reason, DateTimeOffset.UtcNow, currentUser.Id);

        if (dissolution.IsFailure)
            return Result.Fail<DissolveGroupResult>(dissolution.Error!);

        // One event for the group. Consumers holding per-member state derive the
        // closures from the dissolution rather than from N membership events.
        await publisher.PublishAsync(
            new GroupDissolvedEvent(
                TenantId: group.TenantId,
                GroupId: group.Id,
                Reason: reason),
            ct);

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            return Result.Fail<DissolveGroupResult>(CustomerErrors.ConcurrencyConflict);
        }

        return Result.Ok(new DissolveGroupResult(
            GroupId: group.Id,
            Status: group.Status.ToString(),
            DissolvedAt: group.DissolvedAt,
            ClosedMemberships: openMemberships));
    }
}
