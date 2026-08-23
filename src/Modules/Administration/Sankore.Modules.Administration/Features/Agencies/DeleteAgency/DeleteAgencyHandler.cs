using MediatR;
using Microsoft.EntityFrameworkCore;
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

        // Guard: cannot delete an agency that still has active users
        var hasActiveUsers = await db.Users
            .AnyAsync(u => u.AgencyId == request.AgencyId, ct);

        if (hasActiveUsers)
            return Result.Fail("Cannot delete an agency that still has users assigned to it.");

        agency.Deactivate();
        await db.SaveChangesAsync(ct);
        return Result.Ok();
    }
}
