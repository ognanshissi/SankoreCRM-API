namespace Sankore.Modules.Customers.Features.Groups.AssignOfficeRole;

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

internal sealed class AssignOfficeRoleHandler(
    CustomersDbContext db,
    ICurrentUser currentUser,
    IAgencyScopeProvider agencyScope,
    IGroupSizePolicy sizePolicy,
    [FromKeyedServices(nameof(CustomersDbContext))] IEventPublisher publisher
) : IRequestHandler<AssignOfficeRoleCommand, Result<AssignOfficeRoleResult>>
{
    public async Task<Result<AssignOfficeRoleResult>> Handle(
        AssignOfficeRoleCommand request, CancellationToken ct)
    {
        var group = await db.ClientGroups
            .AsTracking()
            .Include(g => g.Memberships)
            .FirstOrDefaultAsync(g => g.Id == request.GroupId, ct);

        if (group is null)
            return Result.Fail<AssignOfficeRoleResult>(CustomerErrors.GroupNotFound);

        if (!await GroupAccess.CanAccessAsync(agencyScope, currentUser.TenantId, currentUser.Id, group, ct))
            return Result.Fail<AssignOfficeRoleResult>(CustomerErrors.GroupNotFound);

        if (request.ExpectedVersion.HasValue && group.Version != request.ExpectedVersion.Value)
            return Result.Fail<AssignOfficeRoleResult>(CustomerErrors.ConcurrencyConflict);

        if (group.Status == GroupStatus.Dissolved)
            return Result.Fail<AssignOfficeRoleResult>(CustomerErrors.InvalidStatusTransition);

        if (!group.Memberships.Any(m => m.ClientId == request.ClientId && m.LeftAt is null))
            return Result.Fail<AssignOfficeRoleResult>(CustomerErrors.MembershipNotFound);

        // Captured before the aggregate reshuffles the roles, so the response can
        // tell the UI which row to repaint as a plain Member.
        var previousHolder = request.OfficeRole == GroupOfficeRole.Member
            ? null
            : group.Memberships
                .FirstOrDefault(m => m.LeftAt is null
                                     && m.OfficeRole == request.OfficeRole
                                     && m.ClientId != request.ClientId)
                ?.ClientId;

        var statusBefore = group.Status;

        // Uniqueness of the office role is an aggregate invariant: ClientGroup
        // demotes the outgoing holder itself, so no handler ever writes two rows
        // with the same role.
        var assignment = group.AssignOfficeRole(request.ClientId, request.OfficeRole, currentUser.Id);

        if (assignment.IsFailure)
            return Result.Fail<AssignOfficeRoleResult>(assignment.Error!);

        // A role change can be the last missing prerequisite, so activation is
        // re-evaluated here exactly as it is after a join.
        var minSize = await sizePolicy.MinSizeAsync(currentUser.TenantId, group.Type, ct);
        group.TryActivate(minSize);

        await publisher.PublishAsync(
            new GroupMembershipChangedEvent(
                TenantId: group.TenantId,
                GroupId: group.Id,
                ClientId: request.ClientId,
                Change: "RoleChanged",
                OfficeRole: request.OfficeRole.ToString()),
            ct);

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            return Result.Fail<AssignOfficeRoleResult>(CustomerErrors.ConcurrencyConflict);
        }

        return Result.Ok(new AssignOfficeRoleResult(
            GroupId: group.Id,
            ClientId: request.ClientId,
            OfficeRole: request.OfficeRole.ToString(),
            GroupStatus: group.Status.ToString(),
            PreviousHolderClientId: previousHolder,
            Activated: statusBefore != group.Status && group.Status == GroupStatus.Active));
    }
}
