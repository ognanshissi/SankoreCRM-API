namespace Sankore.Modules.Customers.Features.Groups.RemoveGroupMember;

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

internal sealed class RemoveGroupMemberHandler(
    CustomersDbContext db,
    ICurrentUser currentUser,
    IAgencyScopeProvider agencyScope,
    IGroupSizePolicy sizePolicy,
    [FromKeyedServices(nameof(CustomersDbContext))] IEventPublisher publisher
) : IRequestHandler<RemoveGroupMemberCommand, Result<RemoveGroupMemberResult>>
{
    public async Task<Result<RemoveGroupMemberResult>> Handle(
        RemoveGroupMemberCommand request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Reason))
            return Result.Fail<RemoveGroupMemberResult>(CustomerErrors.ReasonRequired);

        var group = await db.ClientGroups
            .AsTracking()
            .Include(g => g.Memberships)
            .FirstOrDefaultAsync(g => g.Id == request.GroupId, ct);

        if (group is null)
            return Result.Fail<RemoveGroupMemberResult>(CustomerErrors.GroupNotFound);

        if (!await GroupAccess.CanAccessAsync(agencyScope, currentUser.TenantId, currentUser.Id, group, ct))
            return Result.Fail<RemoveGroupMemberResult>(CustomerErrors.GroupNotFound);

        if (request.ExpectedVersion.HasValue && group.Version != request.ExpectedVersion.Value)
            return Result.Fail<RemoveGroupMemberResult>(CustomerErrors.ConcurrencyConflict);

        // Read before the removal so the outgoing event can carry the role the
        // client held; afterwards the membership is closed.
        var membership = group.Memberships.FirstOrDefault(
            m => m.ClientId == request.ClientId && m.LeftAt is null);

        if (membership is null)
            return Result.Fail<RemoveGroupMemberResult>(CustomerErrors.MembershipNotFound);

        var role = membership.OfficeRole;

        var removal = group.RemoveMember(
            clientId: request.ClientId,
            reason: request.Reason.Trim(),
            at: DateTimeOffset.UtcNow,
            actor: currentUser.Id);

        if (removal.IsFailure)
            return Result.Fail<RemoveGroupMemberResult>(removal.Error!);

        var minSize = await sizePolicy.MinSizeAsync(currentUser.TenantId, group.Type, ct);
        var activeMembers = group.ActiveMemberCount;

        // Only an Active group raises the alert: a Forming group is under its minimum
        // by definition, and a Suspended/Dissolved one is already being handled.
        var belowMinimum = group.Status == GroupStatus.Active && activeMembers < minSize;

        await publisher.PublishAsync(
            new GroupMembershipChangedEvent(
                TenantId: group.TenantId,
                GroupId: group.Id,
                ClientId: request.ClientId,
                Change: "Left",
                OfficeRole: role.ToString()),
            ct);

        if (belowMinimum)
        {
            // Alert only — the group's status is deliberately left untouched.
            await publisher.PublishAsync(
                new ClientUnderMinimumGroupSizeEvent(
                    TenantId: group.TenantId,
                    GroupId: group.Id,
                    ActiveMembers: activeMembers,
                    MinimumSize: minSize),
                ct);
        }

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            return Result.Fail<RemoveGroupMemberResult>(CustomerErrors.ConcurrencyConflict);
        }

        return Result.Ok(new RemoveGroupMemberResult(
            GroupId: group.Id,
            ClientId: request.ClientId,
            GroupStatus: group.Status.ToString(),
            ActiveMemberCount: activeMembers,
            MinimumSize: minSize,
            BelowMinimumSize: belowMinimum));
    }
}
