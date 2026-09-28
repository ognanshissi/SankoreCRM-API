namespace Sankore.Modules.Customers.Features.LegalEntities.CreateLegalClient;

using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Sankore.Modules.Customers.Domain;
using Sankore.Modules.Customers.Domain.Matching;
using Sankore.Modules.Customers.Features.Clients.Shared;
using Sankore.Modules.Customers.Infrastructure;
using Sankore.Modules.Customers.PublicApi.Events;
using Sankore.Shared.Infrastructure.Auth;
using Sankore.Shared.Infrastructure.Crypto;
using Sankore.Shared.Infrastructure.Messaging;
using Sankore.Shared.Kernel;

/// <summary>
/// US-M01-BE-17. Mirrors the individual-creation pipeline (§1.2) step by step so both
/// slices behave identically on duplicates, encryption and outbox publication:
/// legal form → duplicate RCCM → agency code → client number → encryption →
/// aggregate → contact points → event → single commit.
/// </summary>
internal sealed class CreateLegalClientHandler(
    CustomersDbContext db,
    ICurrentUser currentUser,
    IDuplicateProbe duplicateProbe,
    IAgencyDirectory agencyDirectory,
    IClientNumberGenerator clientNumberGenerator,
    IFieldEncryptor encryptor,
    IBlindIndexer indexer,
    IPhoneticKeyCalculator phoneticKeys,
    [FromKeyedServices(nameof(CustomersDbContext))] IEventPublisher publisher
) : IRequestHandler<CreateLegalClientCommand, Result<CreateLegalClientResult>>
{
    public async Task<Result<CreateLegalClientResult>> Handle(
        CreateLegalClientCommand cmd, CancellationToken ct)
    {
        var tenantId = currentUser.TenantId;
        var actor = currentUser.Id;

        // 1. Closed, tenant-configurable list of legal forms: an unknown or deactivated
        //    code is rejected outright (US-M01-BE-17 acceptance criterion).
        var legalFormCode = cmd.LegalFormCode.Trim();
        var legalFormKnown = await db.LegalForms
            .AnyAsync(f => f.Code == legalFormCode && f.IsActive, ct);

        if (!legalFormKnown)
            return Result.Fail<CreateLegalClientResult>(CustomerErrors.LegalFormUnknown);

        // 2. Duplicate RCCM inside the tenant — EQUALITY ON THE BLIND INDEX, never a
        //    decryption of the stored column. A hit is a successful result carrying the
        //    existing client so the operator can open it (endpoint answers 409).
        var registrationNumber = cmd.RegistrationNumber.Trim();
        var registrationBlindIndex = indexer.Compute(
            BlindIndexPurpose.RegistrationNumber, registrationNumber);

        var existing = await duplicateProbe.FindByRegistrationNumberAsync(
            tenantId, registrationBlindIndex, excludeClientId: null, ct);

        if (existing is not null)
        {
            return Result.Ok(new CreateLegalClientResult(
                CreateLegalClientOutcome.BlockedDuplicateRegistrationNumber,
                ClientId: null,
                ClientNumber: null,
                Code: CustomerErrors.DuplicateRegistrationNumber,
                Candidates: [existing]));
        }

        // 3. Agency code. An agency the directory cannot resolve is, by definition,
        //    outside the caller's perimeter.
        var agencyCode = await agencyDirectory.GetAgencyCodeAsync(tenantId, cmd.AgencyId, ct);
        if (string.IsNullOrWhiteSpace(agencyCode))
            return Result.Fail<CreateLegalClientResult>(CustomerErrors.AgencyOutOfScope);

        // 4. Client number (tenant + agency + year sequence).
        var clientNumber = await clientNumberGenerator.NextAsync(tenantId, agencyCode, ct);

        // 5. Protection of the regulated identifiers. The NIF is encrypted but NOT
        //    blind-indexed: nothing searches by tax id, and every extra index widens
        //    the surface of a deterministic-equality oracle.
        var encryptedRegistrationNumber = encryptor.Encrypt(registrationNumber);
        var encryptedTaxIdNumber = string.IsNullOrWhiteSpace(cmd.TaxIdNumber)
            ? null
            : encryptor.Encrypt(cmd.TaxIdNumber.Trim());

        var client = Client.CreateLegal(
            tenantId: tenantId,
            clientNumber: clientNumber,
            agencyId: cmd.AgencyId,
            agencyCode: agencyCode,
            advisorUserId: cmd.AdvisorUserId,
            legalName: cmd.LegalName.Trim(),
            legalFormCode: legalFormCode,
            encryptedRegistrationNumber: encryptedRegistrationNumber,
            registrationNumberBlindIndex: registrationBlindIndex,
            encryptedTaxIdNumber: encryptedTaxIdNumber,
            incorporationDate: cmd.IncorporationDate,
            // Blank falls back to the module default inside the aggregate.
            preferredLanguage: cmd.PreferredLanguage ?? string.Empty,
            createdBy: actor);

        // 6. A company has one name, so only the primary phonetic key is meaningful.
        client.SetPhoneticKeys(phoneticKeys.Compute(cmd.LegalName), null);

        // 7. Contact points: every value encrypted and blind-indexed; the first phone
        //    becomes the primary one.
        var now = DateTimeOffset.UtcNow;
        var isFirstPhone = true;
        foreach (var rawPhone in cmd.PhoneNumbers ?? [])
        {
            if (string.IsNullOrWhiteSpace(rawPhone)) continue;

            var normalized = SensitiveValueNormalizer.NormalizePhone(rawPhone);
            client.AddContactPoint(
                ContactPointType.Phone,
                encryptor.Encrypt(rawPhone.Trim())!,
                indexer.Compute(BlindIndexPurpose.Phone, normalized),
                label: null,
                isPrimary: isFirstPhone,
                validFrom: now,
                actor: actor);

            isFirstPhone = false;
        }

        if (!string.IsNullOrWhiteSpace(cmd.Email))
        {
            var email = cmd.Email.Trim();
            client.AddContactPoint(
                ContactPointType.Email,
                encryptor.Encrypt(email)!,
                indexer.Compute(BlindIndexPurpose.Email, SensitiveValueNormalizer.NormalizeEmail(email)),
                label: null,
                isPrimary: true,
                validFrom: now,
                actor: actor);
        }

        // Same canonical single-line rendering as the individual slice, so the very same
        // address typed on either form yields the very same blind index.
        var headOffice = cmd.HeadOfficeAddress?.ToSingleLineOrNull();
        if (!string.IsNullOrWhiteSpace(headOffice))
        {
            client.AddContactPoint(
                ContactPointType.Address,
                encryptor.Encrypt(headOffice)!,
                indexer.Compute(BlindIndexPurpose.PostalAddress, SensitiveValueNormalizer.NormalizeAddress(headOffice)),
                label: "HeadOffice",
                isPrimary: true,
                validFrom: now,
                actor: actor);
        }

        db.Clients.Add(client);

        // 8. Outbox BEFORE SaveChanges: the publisher only enqueues the row, so the
        //    event and the client are committed by the very same transaction.
        await publisher.PublishAsync(
            new ClientCreatedEvent(
                TenantId: tenantId,
                ClientId: client.Id,
                ClientNumber: client.ClientNumber,
                ClientType: nameof(ClientType.Legal),
                AgencyId: client.AgencyId,
                AdvisorUserId: client.AdvisorUserId,
                SourceLeadId: null,
                CreatedBy: actor),
            ct);

        await db.SaveChangesAsync(ct);

        return Result.Ok(new CreateLegalClientResult(
            CreateLegalClientOutcome.Created,
            client.Id,
            client.ClientNumber,
            Code: null,
            Candidates: []));
    }
}
