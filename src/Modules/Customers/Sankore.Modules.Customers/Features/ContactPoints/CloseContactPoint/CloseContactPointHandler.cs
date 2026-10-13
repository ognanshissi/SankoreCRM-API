using MediatR;
using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Customers.Domain;
using Sankore.Modules.Customers.Features.ContactPoints.Shared;
using Sankore.Modules.Customers.Infrastructure;
using Sankore.Shared.Infrastructure.Auth;
using Sankore.Shared.Kernel;
using Sankore.Shared.Kernel.Authorization;

namespace Sankore.Modules.Customers.Features.ContactPoints.CloseContactPoint;

internal sealed class CloseContactPointHandler(
    CustomersDbContext db,
    ICurrentUser currentUser,
    IAgencyScopeProvider agencyScope,
    ContactPointChangeAnnouncer announcer
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
        // Read BEFORE the closure: afterwards the row is dated out and the type would have to be
        // recovered from a collection the aggregate has already changed.
        var closedType = client.ContactPoints
            .FirstOrDefault(cp => cp.Id == request.ContactPointId)?.Type;

        var closure = client.CloseContactPoint(
            request.ContactPointId, currentUser.Id, DateTimeOffset.UtcNow);

        if (closure.IsFailure)
            return closure;

        // KRM-B-04: closing the address a collection link was posted to leaves that link in a
        // mailbox the client no longer reads.
        if (closedType is { } type)
        {
            await announcer.AnnounceAsync(
                currentUser.TenantId, client.Id, type, currentUser.Id,
                "Coordonnée clôturée", ct);
        }

        await db.SaveChangesAsync(ct);
        return Result.Ok();
    }
}
