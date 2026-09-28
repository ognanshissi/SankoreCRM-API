using MediatR;
using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Customers.Domain;
using Sankore.Modules.Customers.Infrastructure;
using Sankore.Shared.Infrastructure.Auth;
using Sankore.Shared.Kernel;
using Sankore.Shared.Kernel.Authorization;

namespace Sankore.Modules.Customers.Features.ContactPoints.PromoteContactPointToPrimary;

internal sealed class PromoteContactPointToPrimaryHandler(
    CustomersDbContext db,
    ICurrentUser currentUser,
    IAgencyScopeProvider agencyScope
) : IRequestHandler<PromoteContactPointToPrimaryCommand, Result>
{
    public async Task<Result> Handle(PromoteContactPointToPrimaryCommand request, CancellationToken ct)
    {
        var client = await db.Clients
            .AsTracking()
            .Include(c => c.ContactPoints)
            .FirstOrDefaultAsync(c => c.Id == request.ClientId, ct);

        if (client is null ||
            !await agencyScope.CanAccessAgencyAsync(currentUser.TenantId, currentUser.Id, client.AgencyId, ct))
            return Result.Fail(CustomerErrors.ClientNotFound);

        if (client.IsReadOnly)
            return Result.Fail(CustomerErrors.ClientReadOnly);

        // The aggregate demotes the former primary of the same type and rejects an
        // unknown or already closed contact point with CONTACT_POINT_NOT_FOUND.
        var promotion = client.PromoteContactPointToPrimary(request.ContactPointId, currentUser.Id);
        if (promotion.IsFailure)
            return promotion;

        await db.SaveChangesAsync(ct);
        return Result.Ok();
    }
}
