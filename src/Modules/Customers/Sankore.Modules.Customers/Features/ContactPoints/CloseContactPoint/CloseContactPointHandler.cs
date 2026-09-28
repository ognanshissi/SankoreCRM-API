using MediatR;
using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Customers.Domain;
using Sankore.Modules.Customers.Infrastructure;
using Sankore.Shared.Infrastructure.Auth;
using Sankore.Shared.Kernel;
using Sankore.Shared.Kernel.Authorization;

namespace Sankore.Modules.Customers.Features.ContactPoints.CloseContactPoint;

internal sealed class CloseContactPointHandler(
    CustomersDbContext db,
    ICurrentUser currentUser,
    IAgencyScopeProvider agencyScope
) : IRequestHandler<CloseContactPointCommand, Result>
{
    public async Task<Result> Handle(CloseContactPointCommand request, CancellationToken ct)
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

        // The aggregate owns the rules: CONTACT_POINT_NOT_FOUND for an unknown or
        // already closed id, LAST_PHONE_REQUIRED for the last reachable phone, and
        // the promotion of a successor when the closed one was primary. It only ever
        // dates ValidTo — nothing is physically deleted.
        var closure = client.CloseContactPoint(
            request.ContactPointId, currentUser.Id, DateTimeOffset.UtcNow);

        if (closure.IsFailure)
            return closure;

        await db.SaveChangesAsync(ct);
        return Result.Ok();
    }
}
