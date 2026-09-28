namespace Sankore.Modules.Customers.Features.LegalEntities.DeclareBeneficialOwners;

using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Sankore.Modules.Customers.Domain;
using Sankore.Modules.Customers.Infrastructure;
using Sankore.Modules.Customers.PublicApi.Events;
using Sankore.Shared.Infrastructure.Auth;
using Sankore.Shared.Infrastructure.Crypto;
using Sankore.Shared.Infrastructure.Messaging;
using Sankore.Shared.Kernel;
using Sankore.Shared.Kernel.Authorization;

/// <summary>
/// US-M01-BE-18. The declaration replaces the whole structure at once, which is the only
/// way a "who ultimately controls this company" statement can stay consistent: an
/// incremental API would let an operator forget to remove a former shareholder.
/// </summary>
internal sealed class DeclareBeneficialOwnersHandler(
    CustomersDbContext db,
    ICurrentUser currentUser,
    IAgencyScopeProvider agencyScope,
    ICustomerSettings settings,
    IFieldEncryptor encryptor,
    IBlindIndexer indexer,
    [FromKeyedServices(nameof(CustomersDbContext))] IEventPublisher publisher
) : IRequestHandler<DeclareBeneficialOwnersCommand, Result<DeclareBeneficialOwnersResult>>
{
    public async Task<Result<DeclareBeneficialOwnersResult>> Handle(
        DeclareBeneficialOwnersCommand cmd, CancellationToken ct)
    {
        var tenantId = currentUser.TenantId;
        var actor = currentUser.Id;

        if (string.IsNullOrWhiteSpace(cmd.Reason))
            return Result.Fail<DeclareBeneficialOwnersResult>(CustomerErrors.ReasonRequired);

        var client = await db.Clients
            .AsTracking()
            .FirstOrDefaultAsync(c => c.Id == cmd.ClientId, ct);

        if (client is null)
            return Result.Fail<DeclareBeneficialOwnersResult>(CustomerErrors.ClientNotFound);

        // Outside the caller's agency perimeter answers NOT FOUND, never FORBIDDEN:
        // a 403 would confirm that this client id exists.
        var inScope = await agencyScope.CanAccessAgencyAsync(tenantId, actor, client.AgencyId, ct);
        if (!inScope)
            return Result.Fail<DeclareBeneficialOwnersResult>(CustomerErrors.ClientNotFound);

        if (client.Type != ClientType.Legal)
            return Result.Fail<DeclareBeneficialOwnersResult>(LegalEntityErrorCodes.ClientNotLegalEntity);

        if (client.IsReadOnly)
            return Result.Fail<DeclareBeneficialOwnersResult>(CustomerErrors.ClientReadOnly);

        var declared = (cmd.Owners ?? []).ToList();

        // ── AML rule 1: the cake cannot exceed 100 % ────────────────────────────
        var totalOwnership = declared.Sum(o => o.OwnershipPercentage);
        if (totalOwnership > 100m)
            return Result.Fail<DeclareBeneficialOwnersResult>(CustomerErrors.OwnershipExceeds100);

        // ── AML rule 2: when nobody reaches the tenant threshold (default 25 %),
        //    control is exercised otherwise, so at least one manager must be named.
        var threshold = await settings.GetDecimalAsync(
            tenantId, CustomerSettingKeys.BeneficialOwnerThreshold, ct);

        var someoneReachesThreshold = declared.Any(o => o.OwnershipPercentage >= threshold);
        var hasManager = declared.Any(o => o.ControlType == ControlType.Manager);

        if (!someoneReachesThreshold && !hasManager)
            return Result.Fail<DeclareBeneficialOwnersResult>(CustomerErrors.ManagerBeneficialOwnerRequired);

        // ── Set-based reconciliation ────────────────────────────────────────────
        var active = await db.BeneficialOwners
            .AsTracking()
            .Where(o => o.LegalClientId == client.Id && o.ValidTo == null)
            .ToListAsync(ct);

        var now = DateTimeOffset.UtcNow;

        // The comparison key covers the identity AND the declared stake: changing the
        // percentage or the control type of the same person is a NEW state of the world,
        // so the previous row is closed and a new one opened (temporal history, no update).
        var declaredByKey = new Dictionary<string, BeneficialOwnerInput>(StringComparer.Ordinal);
        var declaredIndexes = new Dictionary<string, (string? EncryptedDoc, string? DocBlindIndex)>(StringComparer.Ordinal);

        foreach (var owner in declared)
        {
            string? encryptedDoc = null;
            string? docBlindIndex = null;

            if (!string.IsNullOrWhiteSpace(owner.ExternalDocumentNumber))
            {
                var document = owner.ExternalDocumentNumber.Trim();
                encryptedDoc = encryptor.Encrypt(document);
                docBlindIndex = indexer.Compute(
                    BlindIndexPurpose.IdentityDocument,
                    SensitiveValueNormalizer.NormalizeDocumentNumber(document));
            }

            var key = StateKey(
                IdentityKey(owner.LinkedClientId, docBlindIndex, owner.ExternalFullName),
                owner.OwnershipPercentage,
                owner.ControlType);

            // A duplicate line in the payload declares the same state twice: keep one.
            declaredByKey[key] = owner;
            declaredIndexes[key] = (encryptedDoc, docBlindIndex);
        }

        var closed = 0;
        var survivingKeys = new HashSet<string>(StringComparer.Ordinal);

        foreach (var existing in active)
        {
            var key = StateKey(
                IdentityKey(existing.LinkedClientId, existing.ExternalDocumentBlindIndex, existing.ExternalFullName),
                existing.OwnershipPercentage,
                existing.ControlType);

            if (declaredByKey.ContainsKey(key))
            {
                survivingKeys.Add(key);
                continue;
            }

            // Closed, never removed: a regulator must still be able to read who owned
            // what, and when.
            existing.Close(now);
            closed++;
        }

        var created = 0;
        foreach (var (key, owner) in declaredByKey)
        {
            if (survivingKeys.Contains(key)) continue;

            var (encryptedDoc, docBlindIndex) = declaredIndexes[key];

            var row = owner.LinkedClientId is { } linkedClientId && linkedClientId != Guid.Empty
                ? BeneficialOwner.ForClient(
                    tenantId: tenantId,
                    legalClientId: client.Id,
                    linkedClientId: linkedClientId,
                    ownershipPercentage: owner.OwnershipPercentage,
                    controlType: owner.ControlType,
                    validFrom: now,
                    createdBy: actor)
                : BeneficialOwner.ForExternalPerson(
                    tenantId: tenantId,
                    legalClientId: client.Id,
                    externalFullName: owner.ExternalFullName!.Trim(),
                    externalNationality: owner.ExternalNationality,
                    externalDateOfBirth: owner.ExternalDateOfBirth,
                    encryptedExternalDocumentNumber: encryptedDoc,
                    externalDocumentBlindIndex: docBlindIndex,
                    ownershipPercentage: owner.OwnershipPercentage,
                    controlType: owner.ControlType,
                    validFrom: now,
                    createdBy: actor);

            db.BeneficialOwners.Add(row);
            created++;
        }

        var activeCount = active.Count - closed + created;

        await publisher.PublishAsync(
            new BeneficialOwnersChangedEvent(
                TenantId: tenantId,
                LegalClientId: client.Id,
                ActiveOwnerCount: activeCount,
                ActorUserId: actor),
            ct);

        await db.SaveChangesAsync(ct);

        return Result.Ok(new DeclareBeneficialOwnersResult(activeCount, created, closed));
    }

    /// <summary>
    /// Identity of a beneficial owner, best available discriminator first: the linked
    /// client, then the blind index of the identity document, then the normalized name.
    /// Never the clear document number — this runs without decrypting anything.
    /// </summary>
    private static string IdentityKey(Guid? linkedClientId, string? documentBlindIndex, string? externalFullName)
    {
        if (linkedClientId is { } id && id != Guid.Empty)
            return $"client:{id:D}";

        if (!string.IsNullOrWhiteSpace(documentBlindIndex))
            return $"doc:{documentBlindIndex}";

        return $"name:{(externalFullName ?? string.Empty).Trim().ToUpperInvariant()}";
    }

    private static string StateKey(string identityKey, decimal ownership, ControlType controlType)
        => string.Create(
            System.Globalization.CultureInfo.InvariantCulture,
            $"{identityKey}|{ownership:0.##}|{controlType}");
}
