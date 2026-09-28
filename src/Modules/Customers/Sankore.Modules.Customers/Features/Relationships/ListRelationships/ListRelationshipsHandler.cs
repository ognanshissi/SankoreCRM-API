using MediatR;
using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Customers.Domain;
using Sankore.Modules.Customers.Features.Relationships.Shared;
using Sankore.Modules.Customers.Infrastructure;
using Sankore.Shared.Infrastructure.Auth;
using Sankore.Shared.Infrastructure.Crypto;
using Sankore.Shared.Kernel;
using Sankore.Shared.Kernel.Authorization;

namespace Sankore.Modules.Customers.Features.Relationships.ListRelationships;

internal sealed class ListRelationshipsHandler(
    CustomersDbContext db,
    ICurrentUser currentUser,
    IAgencyScopeProvider agencyScope,
    IFieldEncryptor encryptor
) : IRequestHandler<ListRelationshipsQuery, Result<IReadOnlyList<RelationshipDto>>>
{
    public async Task<Result<IReadOnlyList<RelationshipDto>>> Handle(
        ListRelationshipsQuery request, CancellationToken ct)
    {
        var client = await db.Clients
            .Where(c => c.Id == request.ClientId)
            .Select(c => new { c.Id, c.AgencyId })
            .FirstOrDefaultAsync(ct);

        if (client is null ||
            !await agencyScope.CanAccessAgencyAsync(currentUser.TenantId, currentUser.Id, client.AgencyId, ct))
            return Result.Fail<IReadOnlyList<RelationshipDto>>(CustomerErrors.ClientNotFound);

        var query = db.ClientRelationships.Where(r => r.ClientId == request.ClientId);

        if (!request.IncludeClosed)
            query = query.Where(r => r.ValidTo == null);

        var rows = await query
            .OrderBy(r => r.Type)
            .ThenByDescending(r => r.ValidFrom)
            .ToListAsync(ct);

        // One extra round trip resolves the display name of every related client, so
        // the caller does not have to fetch each of them one by one.
        var relatedIds = rows
            .Where(r => r.RelatedClientId is not null)
            .Select(r => r.RelatedClientId!.Value)
            .Distinct()
            .ToList();

        var relatedClients = relatedIds.Count == 0
            ? []
            : await db.Clients
                .Where(c => relatedIds.Contains(c.Id))
                .Select(c => new { c.Id, c.ClientNumber, c.DisplayName })
                .ToDictionaryAsync(c => c.Id, ct);

        // Decryption and masking run in memory: untranslatable to SQL, and the clear
        // value must never reach the response anyway.
        IReadOnlyList<RelationshipDto> dtos =
        [
            .. rows.Select(r =>
            {
                var related = r.RelatedClientId is { } rid && relatedClients.TryGetValue(rid, out var rc)
                    ? rc
                    : null;

                return RelationshipProtection.ToDto(
                    encryptor, r, related?.ClientNumber, related?.DisplayName);
            })
        ];

        return Result.Ok(dtos);
    }
}
