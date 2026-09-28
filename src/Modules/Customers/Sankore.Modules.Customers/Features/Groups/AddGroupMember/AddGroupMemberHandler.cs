namespace Sankore.Modules.Customers.Features.Groups.AddGroupMember;

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

internal sealed class AddGroupMemberHandler(
    CustomersDbContext db,
    ICurrentUser currentUser,
    IAgencyScopeProvider agencyScope,
    IGroupSizePolicy sizePolicy,
    [FromKeyedServices(nameof(CustomersDbContext))] IEventPublisher publisher
) : IRequestHandler<AddGroupMemberCommand, Result<AddGroupMemberResult>>
{
    /// <summary>A client in one of these states may not join a group (GROUP_MEMBER_NOT_ELIGIBLE).</summary>
    private static readonly ClientStatus[] IneligibleStatuses =
        [ClientStatus.KycRejected, ClientStatus.Archived, ClientStatus.Merged];

    public async Task<Result<AddGroupMemberResult>> Handle(
        AddGroupMemberCommand request, CancellationToken ct)
    {
        var group = await db.ClientGroups
            .AsTracking()
            .Include(g => g.Memberships)
            .FirstOrDefaultAsync(g => g.Id == request.GroupId, ct);

        if (group is null)
            return Result.Fail<AddGroupMemberResult>(CustomerErrors.GroupNotFound);

        if (!await GroupAccess.CanAccessAsync(agencyScope, currentUser.TenantId, currentUser.Id, group, ct))
            return Result.Fail<AddGroupMemberResult>(CustomerErrors.GroupNotFound);

        if (request.ExpectedVersion.HasValue && group.Version != request.ExpectedVersion.Value)
            return Result.Fail<AddGroupMemberResult>(CustomerErrors.ConcurrencyConflict);

        if (group.Status == GroupStatus.Dissolved)
            return Result.Fail<AddGroupMemberResult>(CustomerErrors.InvalidStatusTransition);

        var client = await db.Clients
            .Where(c => c.Id == request.ClientId)
            .Select(c => new { c.Status })
            .FirstOrDefaultAsync(ct);

        if (client is null)
            return Result.Fail<AddGroupMemberResult>(CustomerErrors.ClientNotFound);

        if (IneligibleStatuses.Contains(client.Status))
            return Result.Fail<AddGroupMemberResult>(CustomerErrors.GroupMemberNotEligible);

        // Idempotent re-post: the client is already a live member. Returning the
        // existing membership keeps the caller from hitting ux_group_memberships_active
        // as a 500; changing the office role is a different endpoint.
        var existing = group.Memberships.FirstOrDefault(
            m => m.ClientId == request.ClientId && m.LeftAt is null);

        if (existing is not null)
        {
            return Result.Ok(new AddGroupMemberResult(
                existing.Id, group.Id, existing.ClientId, existing.OfficeRole.ToString(),
                group.Status.ToString(), group.ActiveMemberCount, Activated: false));
        }

        var maxSize = await sizePolicy.MaxSizeAsync(currentUser.TenantId, group.Type, ct);
        if (group.ActiveMemberCount >= maxSize)
            return Result.Fail<AddGroupMemberResult>(CustomerErrors.GroupSizeLimitReached);

        // Solidarity groups carry joint liability, so a client belonging to two of
        // them at once would pledge the same guarantee twice. The rule is driven by
        // the tenant setting `solidarity-single-group-rule` because it is still to be
        // CONFIRMED WITH THE BUSINESS: some networks do allow a second group once the
        // first loan cycle is closed. Flipping the setting to false disables the check
        // without a code change or a migration.
        if (group.Type == GroupType.SolidarityGroup
            && await sizePolicy.SolidaritySingleGroupRuleAsync(currentUser.TenantId, ct))
        {
            // Method syntax, not a query expression: `group` is a contextual keyword
            // inside one, and the aggregate variable is named `group` here.
            var currentGroupId = group.Id;
            var clientId = request.ClientId;

            var inAnotherSolidarityGroup = await db.GroupMemberships
                .Join(db.ClientGroups, m => m.GroupId, g => g.Id, (m, g) => new { Membership = m, Group = g })
                .AnyAsync(x => x.Membership.ClientId == clientId
                               && x.Membership.LeftAt == null
                               && x.Group.Id != currentGroupId
                               && x.Group.Type == GroupType.SolidarityGroup
                               && x.Group.Status != GroupStatus.Dissolved, ct);

            if (inAnotherSolidarityGroup)
                return Result.Fail<AddGroupMemberResult>(CustomerErrors.AlreadyInSolidarityGroup);
        }

        var statusBefore = group.Status;
        var membership = group.AddMember(
            clientId: request.ClientId,
            role: request.OfficeRole,
            at: DateTimeOffset.UtcNow,
            actor: currentUser.Id);

        // Automatic activation: Forming -> Active only when the minimum size is
        // reached AND President + Treasurer + Secretary are all filled. A failed
        // Result here simply means "not yet eligible", which is not a caller error.
        var minSize = await sizePolicy.MinSizeAsync(currentUser.TenantId, group.Type, ct);
        group.TryActivate(minSize);

        await publisher.PublishAsync(
            new GroupMembershipChangedEvent(
                TenantId: group.TenantId,
                GroupId: group.Id,
                ClientId: request.ClientId,
                Change: "Joined",
                OfficeRole: membership.OfficeRole.ToString()),
            ct);

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            return Result.Fail<AddGroupMemberResult>(CustomerErrors.ConcurrencyConflict);
        }
        catch (DbUpdateException ex) when (ex.IsViolationOf(GroupUniqueViolation.ActiveMembershipIndex))
        {
            // A concurrent request added the same client first; the caller should
            // re-read the group rather than see a 500.
            return Result.Fail<AddGroupMemberResult>(CustomerErrors.ConcurrencyConflict);
        }

        return Result.Ok(new AddGroupMemberResult(
            MembershipId: membership.Id,
            GroupId: group.Id,
            ClientId: request.ClientId,
            OfficeRole: membership.OfficeRole.ToString(),
            GroupStatus: group.Status.ToString(),
            ActiveMemberCount: group.ActiveMemberCount,
            Activated: statusBefore != group.Status && group.Status == GroupStatus.Active));
    }
}
