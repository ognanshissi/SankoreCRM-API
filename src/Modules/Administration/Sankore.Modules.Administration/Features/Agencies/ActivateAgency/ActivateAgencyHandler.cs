using MediatR;
using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Administration.Infrastructure;
using Sankore.Shared.Infrastructure.Auth;
using Sankore.Shared.Kernel;

namespace Sankore.Modules.Administration.Features.Agencies.ActivateAgency;

internal sealed class ActivateAgencyHandler(
    AdministrationDbContext db,
    ICurrentUser currentUser
) : IRequestHandler<ActivateAgencyCommand, Result>
{
    public async Task<Result> Handle(ActivateAgencyCommand request, CancellationToken ct)
    {
        // IgnoreQueryFilters is required to reach soft-deleted agencies;
        // we re-apply the tenant guard manually.
        var agency = await db.Agencies
            .AsTracking()
            .IgnoreQueryFilters()
            .Where(a => a.Id == request.AgencyId && a.TenantId == currentUser.TenantId)
            .SingleOrDefaultAsync(ct);

        if (agency is null)
            return Result.Fail($"Agency {request.AgencyId} not found.");

        if (!agency.IsDeleted)
            return Result.Fail("Agency is not deactivated.");

        agency.Activate();
        await db.SaveChangesAsync(ct);
        return Result.Ok();
    }
}
