using MediatR;
using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Administration.Domain;
using Sankore.Modules.Administration.Infrastructure;
using Sankore.Shared.Kernel;

namespace Sankore.Modules.Administration.Features.Agencies.AssignAgencyManager;

internal sealed class AssignAgencyManagerHandler(
    AdministrationDbContext db,
    AgencyManagerRoleCoordinator roles,
    AgencyManagerNotifier notifier
) : IRequestHandler<AssignAgencyManagerCommand, Result<AssignAgencyManagerResult>>
{
    public async Task<Result<AssignAgencyManagerResult>> Handle(
        AssignAgencyManagerCommand request, CancellationToken ct)
    {
        var agency = await db.Agencies
            .AsTracking()
            .SingleOrDefaultAsync(a => a.Id == request.AgencyId, ct);

        if (agency is null)
            return Result.Fail<AssignAgencyManagerResult>(AgencyManagerErrors.AgencyNotFound);

        if (agency.IsDeleted)
            return Result.Fail<AssignAgencyManagerResult>(AgencyManagerErrors.AgencyDeleted);

        // The tenant query filter applies here, so a user of another tenant simply does not
        // exist as far as this query is concerned. Tracked, because UserManager needs the entity
        // itself to write the Identity role row.
        var manager = await db.Users
            .AsTracking()
            .SingleOrDefaultAsync(u => u.Id == request.ManagerUserId, ct);

        if (manager is null)
            return Result.Fail<AssignAgencyManagerResult>(AgencyManagerErrors.ManagerNotFound);

        if (manager.Status != UserStatus.Active)
            return Result.Fail<AssignAgencyManagerResult>(AgencyManagerErrors.ManagerNotActive);

        // A manager runs the agency they belong to. Super-users have no agency of their own and
        // operate across the whole tenant, so they are exempt from the membership rule.
        if (!manager.IsSuperUser && manager.AgencyId != agency.Id)
            return Result.Fail<AssignAgencyManagerResult>(AgencyManagerErrors.ManagerNotInAgency);

        var previous = agency.ManagerUserId;
        agency.AssignManager(manager.Id);
        await db.SaveChangesAsync(ct);

        // The agency change is saved first so the revocation below can ask the database whether
        // the outgoing manager still runs anything. Both steps share the ambient TransactionScope
        // opened by TransactionBehavior, so a failure here rolls the assignment back with it.
        var granted = await roles.GrantAsync(manager, agency.TenantId, ct);
        if (granted.IsFailure)
            return Result.Fail<AssignAgencyManagerResult>(granted.Error!);

        if (previous is { } outgoing && outgoing != manager.Id)
        {
            var revoked = await roles.RevokeIfVacatedAsync(outgoing, agency.TenantId, ct);
            if (revoked.IsFailure)
                return Result.Fail<AssignAgencyManagerResult>(revoked.Error!);
        }

        var changed = previous != manager.Id;
        if (changed)
        {
            await notifier.NotifyAssignedAsync(agency, manager, ct);

            if (previous is { } replaced)
                await notifier.NotifyUnassignedAsync(agency, replaced, ct);
        }

        return Result.Ok(new AssignAgencyManagerResult(
            AgencyId: agency.Id,
            ManagerUserId: manager.Id,
            ManagerFullName: manager.FullName,
            PreviousManagerUserId: previous,
            Changed: changed,
            GrantedRole: global::Sankore.Shared.Kernel.Roles.BranchManager.Code));
    }
}
