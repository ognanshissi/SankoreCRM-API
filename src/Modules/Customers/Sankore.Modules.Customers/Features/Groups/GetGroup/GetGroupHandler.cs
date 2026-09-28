namespace Sankore.Modules.Customers.Features.Groups.GetGroup;

using MediatR;
using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Customers.Domain;
using Sankore.Modules.Customers.Features.Groups.Shared;
using Sankore.Modules.Customers.Infrastructure;
using Sankore.Shared.Infrastructure.Auth;
using Sankore.Shared.Kernel;
using Sankore.Shared.Kernel.Authorization;

internal sealed class GetGroupHandler(
    CustomersDbContext db,
    ICurrentUser currentUser,
    IAgencyScopeProvider agencyScope,
    IGroupSizePolicy sizePolicy
) : IRequestHandler<GetGroupQuery, Result<GroupDetailDto>>
{
    public async Task<Result<GroupDetailDto>> Handle(GetGroupQuery request, CancellationToken ct)
    {
        var group = await db.ClientGroups.FirstOrDefaultAsync(g => g.Id == request.GroupId, ct);

        if (group is null)
            return Result.Fail<GroupDetailDto>(CustomerErrors.GroupNotFound);

        // Out of perimeter reads as "not found" on purpose: a 403 would confirm the
        // group exists in another agency of the tenant.
        if (!await GroupAccess.CanAccessAsync(agencyScope, currentUser.TenantId, currentUser.Id, group, ct))
            return Result.Fail<GroupDetailDto>(CustomerErrors.GroupNotFound);

        // Method syntax, not a query expression: `group` is a contextual keyword
        // inside one, and the aggregate variable is named `group` here.
        var groupId = group.Id;
        var includeFormer = request.IncludeFormerMembers;

        var members = await db.GroupMemberships
            .Where(m => m.GroupId == groupId && (includeFormer || m.LeftAt == null))
            .Join(db.Clients, m => m.ClientId, c => c.Id, (m, c) => new { Membership = m, Client = c })
            .OrderBy(x => x.Client.DisplayName)
            .Select(x => new GroupMemberDto(
                x.Membership.Id,
                x.Membership.ClientId,
                x.Client.ClientNumber,
                x.Client.DisplayName,
                x.Client.Status.ToString(),
                x.Membership.OfficeRole.ToString(),
                x.Membership.JoinedAt,
                x.Membership.LeftAt,
                x.Membership.LeaveReason))
            .ToListAsync(ct);

        var activeMembers = members.Count(m => m.LeftAt is null);

        // Surfaced next to the roll so the UI can show "7 / 30 members" and warn
        // before a join is refused with GROUP_SIZE_LIMIT_REACHED.
        var minSize = await sizePolicy.MinSizeAsync(currentUser.TenantId, group.Type, ct);
        var maxSize = await sizePolicy.MaxSizeAsync(currentUser.TenantId, group.Type, ct);

        Guid? RoleHolder(GroupOfficeRole role) => members
            .FirstOrDefault(m => m.LeftAt is null && m.OfficeRole == role.ToString())?.ClientId;

        return Result.Ok(new GroupDetailDto(
            Id: group.Id,
            Name: group.Name,
            Type: group.Type.ToString(),
            Status: group.Status.ToString(),
            AgencyId: group.AgencyId,
            ConstitutionDate: group.ConstitutionDate,
            DissolutionReason: group.DissolutionReason,
            DissolvedAt: group.DissolvedAt,
            ActiveMemberCount: activeMembers,
            MinimumSize: minSize,
            MaximumSize: maxSize,
            PresidentClientId: RoleHolder(GroupOfficeRole.President),
            TreasurerClientId: RoleHolder(GroupOfficeRole.Treasurer),
            SecretaryClientId: RoleHolder(GroupOfficeRole.Secretary),
            Version: group.Version,
            CreatedAt: group.CreatedAt,
            UpdatedAt: group.UpdatedAt,
            Members: members));
    }
}
