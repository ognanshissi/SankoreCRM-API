using MediatR;
using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Customers.Domain;
using Sankore.Modules.Customers.Features.ContactPoints.Shared;
using Sankore.Modules.Customers.Infrastructure;
using Sankore.Shared.Infrastructure.Auth;
using Sankore.Shared.Infrastructure.Crypto;
using Sankore.Shared.Kernel;
using Sankore.Shared.Kernel.Authorization;

namespace Sankore.Modules.Customers.Features.ContactPoints.ListContactPoints;

internal sealed class ListContactPointsHandler(
    CustomersDbContext db,
    ICurrentUser currentUser,
    IAgencyScopeProvider agencyScope,
    IFieldEncryptor encryptor
) : IRequestHandler<ListContactPointsQuery, Result<IReadOnlyList<ContactPointDto>>>
{
    public async Task<Result<IReadOnlyList<ContactPointDto>>> Handle(
        ListContactPointsQuery request, CancellationToken ct)
    {
        var client = await db.Clients
            .Where(c => c.Id == request.ClientId)
            .Select(c => new { c.Id, c.AgencyId })
            .FirstOrDefaultAsync(ct);

        if (client is null ||
            !await agencyScope.CanAccessAgencyAsync(currentUser.TenantId, currentUser.Id, client.AgencyId, ct))
            return Result.Fail<IReadOnlyList<ContactPointDto>>(CustomerErrors.ClientNotFound);

        var query = db.ClientContactPoints.Where(cp => cp.ClientId == request.ClientId);

        if (!request.IncludeClosed)
            query = query.Where(cp => cp.ValidTo == null);

        var rows = await query
            .OrderBy(cp => cp.Type)
            .ThenByDescending(cp => cp.IsPrimary)
            .ThenByDescending(cp => cp.ValidFrom)
            .ToListAsync(ct);

        // Decryption and masking happen in memory: neither operation is translatable
        // to SQL, and the clear value must never reach the response anyway.
        IReadOnlyList<ContactPointDto> dtos =
            [.. rows.Select(cp => ContactPointProtection.ToDto(encryptor, cp))];

        return Result.Ok(dtos);
    }
}
