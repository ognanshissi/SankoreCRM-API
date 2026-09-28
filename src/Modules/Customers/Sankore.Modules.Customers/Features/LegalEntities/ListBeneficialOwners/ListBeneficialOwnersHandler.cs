namespace Sankore.Modules.Customers.Features.LegalEntities.ListBeneficialOwners;

using MediatR;
using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Customers.Domain;
using Sankore.Modules.Customers.Infrastructure;
using Sankore.Shared.Infrastructure.Auth;
using Sankore.Shared.Infrastructure.Crypto;
using Sankore.Shared.Kernel;
using Sankore.Shared.Kernel.Authorization;

internal sealed class ListBeneficialOwnersHandler(
    CustomersDbContext db,
    ICurrentUser currentUser,
    IAgencyScopeProvider agencyScope,
    IFieldEncryptor encryptor
) : IRequestHandler<ListBeneficialOwnersQuery, Result<IReadOnlyList<BeneficialOwnerDto>>>
{
    public async Task<Result<IReadOnlyList<BeneficialOwnerDto>>> Handle(
        ListBeneficialOwnersQuery query, CancellationToken ct)
    {
        var tenantId = currentUser.TenantId;

        var client = await db.Clients
            .Where(c => c.Id == query.ClientId)
            .Select(c => new { c.Id, c.AgencyId, c.Type })
            .FirstOrDefaultAsync(ct);

        if (client is null)
            return Result.Fail<IReadOnlyList<BeneficialOwnerDto>>(CustomerErrors.ClientNotFound);

        // Out of perimeter reads as "does not exist" (§7 of the design contract).
        var inScope = await agencyScope.CanAccessAgencyAsync(tenantId, currentUser.Id, client.AgencyId, ct);
        if (!inScope)
            return Result.Fail<IReadOnlyList<BeneficialOwnerDto>>(CustomerErrors.ClientNotFound);

        if (client.Type != ClientType.Legal)
            return Result.Fail<IReadOnlyList<BeneficialOwnerDto>>(LegalEntityErrorCodes.ClientNotLegalEntity);

        var rows = await db.BeneficialOwners
            .Where(o => o.LegalClientId == client.Id)
            .Where(o => query.IncludeClosed || o.ValidTo == null)
            .OrderByDescending(o => o.ValidTo == null)
            .ThenByDescending(o => o.OwnershipPercentage)
            .ThenBy(o => o.ValidFrom)
            .Select(o => new
            {
                o.Id,
                o.LinkedClientId,
                o.ExternalFullName,
                o.ExternalNationality,
                o.ExternalDateOfBirth,
                o.EncryptedExternalDocumentNumber,
                o.OwnershipPercentage,
                o.ControlType,
                o.ValidFrom,
                o.ValidTo,
            })
            .ToListAsync(ct);

        // One extra round-trip instead of a cross-entity join: the linked clients are a
        // handful of rows and the join would fight the tenant query filter for nothing.
        var linkedIds = rows
            .Where(r => r.LinkedClientId.HasValue)
            .Select(r => r.LinkedClientId!.Value)
            .Distinct()
            .ToList();

        var linked = linkedIds.Count == 0
            ? []
            : await db.Clients
                .Where(c => linkedIds.Contains(c.Id))
                .Select(c => new { c.Id, c.ClientNumber, c.DisplayName })
                .ToListAsync(ct);

        var linkedById = linked.ToDictionary(c => c.Id);

        var dtos = rows.Select(r =>
        {
            linkedById.TryGetValue(r.LinkedClientId ?? Guid.Empty, out var linkedClient);

            return new BeneficialOwnerDto(
                Id: r.Id,
                LinkedClientId: r.LinkedClientId,
                LinkedClientNumber: linkedClient?.ClientNumber,
                LinkedClientDisplayName: linkedClient?.DisplayName,
                ExternalFullName: r.ExternalFullName,
                ExternalNationality: r.ExternalNationality,
                ExternalDateOfBirthMasked: SensitiveValueMasker.MaskDate(r.ExternalDateOfBirth),
                // Decrypt then mask: the clear value never reaches the response body.
                ExternalDocumentNumberMasked: SensitiveValueMasker.MaskDocument(
                    encryptor.Decrypt(r.EncryptedExternalDocumentNumber)),
                OwnershipPercentage: r.OwnershipPercentage,
                ControlType: r.ControlType.ToString(),
                ValidFrom: r.ValidFrom,
                ValidTo: r.ValidTo,
                IsActive: r.ValidTo is null);
        }).ToList();

        return Result.Ok<IReadOnlyList<BeneficialOwnerDto>>(dtos);
    }
}
