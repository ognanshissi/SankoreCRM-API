using MediatR;
using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Administration.Infrastructure;
using Sankore.Shared.Kernel;

namespace Sankore.Modules.Administration.Features.Agencies.AssignAgencyManager;

internal sealed class RemoveAgencyManagerHandler(
    AdministrationDbContext db,
    AgencyManagerRoleCoordinator roles,
    AgencyManagerNotifier notifier
) : IRequestHandler<RemoveAgencyManagerCommand, Result>
{
    public async Task<Result> Handle(RemoveAgencyManagerCommand request, CancellationToken ct)
    {
        var agency = await db.Agencies
            .AsTracking()
            .SingleOrDefaultAsync(a => a.Id == request.AgencyId, ct);

        if (agency is null)
            return Result.Fail(AgencyManagerErrors.AgencyNotFound);

        // A deleted agency is not rejected here: clearing the post of an agency that has been
        // closed is a tidy-up, not a change of responsibility.
        var outgoing = agency.ManagerUserId;
        agency.RemoveManager();
        await db.SaveChangesAsync(ct);

        // Saved first, so the coordinator's "does this person still run an agency?" check sees
        // the vacated post rather than the state we are leaving behind.
        if (outgoing is { } previousManager)
        {
            var revoked = await roles.RevokeIfVacatedAsync(previousManager, agency.TenantId, ct);
            if (revoked.IsFailure)
                return revoked;

            await notifier.NotifyUnassignedAsync(agency, previousManager, ct);
        }

        return Result.Ok();
    }
}
