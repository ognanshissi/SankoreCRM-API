using MediatR;
using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Customers.Domain;
using Sankore.Modules.Customers.Features.ContactPoints.Shared;
using Sankore.Modules.Customers.Infrastructure;
using Sankore.Shared.Infrastructure.Auth;
using Sankore.Shared.Infrastructure.Crypto;
using Sankore.Shared.Kernel;
using Sankore.Shared.Kernel.Authorization;

namespace Sankore.Modules.Customers.Features.ContactPoints.AddContactPoint;

internal sealed class AddContactPointHandler(
    CustomersDbContext db,
    ICurrentUser currentUser,
    IAgencyScopeProvider agencyScope,
    IFieldEncryptor encryptor,
    IBlindIndexer indexer
) : IRequestHandler<AddContactPointCommand, Result<AddContactPointResult>>
{
    public async Task<Result<AddContactPointResult>> Handle(
        AddContactPointCommand request, CancellationToken ct)
    {
        var client = await db.Clients
            .AsTracking()
            .Include(c => c.ContactPoints)
            .FirstOrDefaultAsync(c => c.Id == request.ClientId, ct);

        // A client outside the caller's agency perimeter is reported as missing,
        // never as forbidden: a 403 would confirm that the record exists.
        if (client is null ||
            !await agencyScope.CanAccessAgencyAsync(currentUser.TenantId, currentUser.Id, client.AgencyId, ct))
            return Result.Fail<AddContactPointResult>(CustomerErrors.ClientNotFound);

        if (client.IsReadOnly)
            return Result.Fail<AddContactPointResult>(CustomerErrors.ClientReadOnly);

        var value = request.Value.Trim();

        // The indexer normalizes the value itself for the given purpose, so the same
        // number typed "+225 07 08 09 18" or "0708091 8" lands on the same index.
        var blindIndex = indexer.Compute(ContactPointProtection.PurposeFor(request.Type), value);

        // ── Idempotence ─────────────────────────────────────────────────────
        // Re-posting a value already on file for the same type returns the existing
        // row instead of creating a duplicate. Only makePrimary is still honoured,
        // because asking for a primary that is not primary yet is a real intent.
        var existing = client.ContactPoints
            .FirstOrDefault(cp => cp.ValidTo is null && cp.Type == request.Type && cp.BlindIndex == blindIndex);

        if (existing is not null)
        {
            if (request.MakePrimary && !existing.IsPrimary)
            {
                var promotion = client.PromoteContactPointToPrimary(existing.Id, currentUser.Id);
                if (promotion.IsFailure)
                    return Result.Fail<AddContactPointResult>(promotion.Error!);

                await db.SaveChangesAsync(ct);
            }

            return Result.Ok(new AddContactPointResult(
                existing.Id,
                existing.Type,
                ContactPointProtection.MaskEncrypted(encryptor, existing.Type, existing.EncryptedValue),
                existing.IsPrimary,
                existing.ValidFrom,
                AlreadyExisted: true));
        }

        // Only the ciphertext and the blind index ever reach the database. The clear
        // value stops here.
        var encrypted = encryptor.Encrypt(value)!;

        // The aggregate owns the primary-uniqueness invariant (it demotes the former
        // primary and auto-promotes the first contact point of a type).
        var added = client.AddContactPoint(
            type: request.Type,
            encryptedValue: encrypted,
            blindIndex: blindIndex,
            label: request.Label,
            isPrimary: request.MakePrimary,
            validFrom: DateTimeOffset.UtcNow,
            actor: currentUser.Id);

        await db.SaveChangesAsync(ct);

        return Result.Ok(new AddContactPointResult(
            added.Id,
            added.Type,
            ContactPointProtection.Mask(request.Type, value),
            added.IsPrimary,
            added.ValidFrom,
            AlreadyExisted: false));
    }
}
