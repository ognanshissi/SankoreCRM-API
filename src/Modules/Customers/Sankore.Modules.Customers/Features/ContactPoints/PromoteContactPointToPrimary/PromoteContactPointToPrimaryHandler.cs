using MediatR;
using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Customers.Domain;
using Sankore.Modules.Customers.Features.ContactPoints.Shared;
using Sankore.Modules.Customers.Infrastructure;
using Sankore.Shared.Infrastructure.Auth;
using Sankore.Shared.Kernel;
using Sankore.Shared.Kernel.Authorization;

namespace Sankore.Modules.Customers.Features.ContactPoints.PromoteContactPointToPrimary;

internal sealed class PromoteContactPointToPrimaryHandler(
    CustomersDbContext db,
    ICurrentUser currentUser,
    IAgencyScopeProvider agencyScope,
    ContactPointChangeAnnouncer announcer
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
        var promotedType = client.ContactPoints
            .FirstOrDefault(cp => cp.Id == request.ContactPointId)?.Type;

        var promotion = client.PromoteContactPointToPrimary(request.ContactPointId, currentUser.Id);
        if (promotion.IsFailure)
            return promotion;

        // KRM-B-04: which address is primary decides where the next message goes, and whether the
        // link already sent went somewhere that still counts.
        if (promotedType is { } type)
        {
            await announcer.AnnounceAsync(
                currentUser.TenantId, client.Id, type, currentUser.Id,
                "Coordonnée promue en principale", ct);
        }

        await db.SaveChangesAsync(ct);
        return Result.Ok();
    }
}
