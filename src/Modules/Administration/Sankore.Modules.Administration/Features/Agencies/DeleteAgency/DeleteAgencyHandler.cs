using MediatR;
using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Administration.Domain;
using Sankore.Modules.Administration.Infrastructure;
using Sankore.Shared.Kernel;

namespace Sankore.Modules.Administration.Features.Agencies.DeleteAgency;

internal sealed class DeleteAgencyHandler(
    AdministrationDbContext db
) : IRequestHandler<DeleteAgencyCommand, Result>
{
    public async Task<Result> Handle(DeleteAgencyCommand request, CancellationToken ct)
    {
        var agency = await db.Agencies
            .AsTracking()
            .Where(a => a.Id == request.AgencyId)
            .SingleOrDefaultAsync(ct);

        if (agency is null)
            return Result.Fail($"Agency {request.AgencyId} not found.");

        if (agency.IsDeleted)
            return Result.Fail("Agency is already deleted.");

        // Guard: AGENCY_HAS_ACTIVE_USERS — only non-disabled users block deletion
        var hasActiveUsers = await db.Users
            .AnyAsync(u => u.AgencyId == request.AgencyId
                        && u.Status != UserStatus.Disabled, ct);

        if (hasActiveUsers)
            return Result.Fail("AGENCY_HAS_ACTIVE_USERS: Cannot delete an agency that still has active users. Reassign or deactivate them first.");

        // Guard: cannot delete a parent that still has active child agencies
        var hasChildren = await db.Agencies
            .AnyAsync(a => a.ParentAgencyId == request.AgencyId && !a.IsDeleted, ct);

        if (hasChildren)
            return Result.Fail("Cannot delete an agency that still has active child agencies.");

        agency.Deactivate();
        await db.SaveChangesAsync(ct);
        return Result.Ok();
    }
}
