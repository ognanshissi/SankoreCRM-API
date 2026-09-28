namespace Sankore.Modules.Customers.Features.Clients.CreateIndividualClient;

using System.Globalization;
using MediatR;
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
/// US-M01-BE-04 + US-M01-BE-05. Runs a fixed pipeline whose ORDER is part of the
/// specification: age → duplicate document → duplicate phone → agency code → client
/// number → encryption → aggregate → contact points → outbox → commit.
///
/// The cheap, purely computational rejection (age) comes first, then the two duplicate
/// probes, and only then the steps that CONSUME a resource: the client-number sequence
/// is bumped last, so a rejected creation never burns a number and leaves a hole in the
/// tenant's numbering.
///
/// Nothing in this handler ever decrypts. Duplicate detection is exact equality on the
/// blind-index columns, which is both indexable and non-revealing — a dedicated test
/// asserts <c>IFieldEncryptor.Decrypt</c> is called zero times on this path.
/// </summary>
internal sealed class CreateIndividualClientHandler(
    CustomersDbContext db,
    ICurrentUser currentUser,
    ICustomerSettings settings,
    IDuplicateProbe duplicateProbe,
    IAgencyDirectory agencyDirectory,
    IClientNumberGenerator clientNumberGenerator,
    IFieldEncryptor encryptor,
    IBlindIndexer indexer,
    IPhoneticKeyCalculator phoneticKeys,
    [FromKeyedServices(nameof(CustomersDbContext))] IEventPublisher publisher
) : IRequestHandler<CreateIndividualClientCommand, Result<CreateClientResult>>
{
    public async Task<Result<CreateClientResult>> Handle(
        CreateIndividualClientCommand cmd, CancellationToken ct)
    {
        var tenantId = currentUser.TenantId;
        var actor = currentUser.Id;
        var now = DateTimeOffset.UtcNow;

        // ── 1. Minimum age (tenant setting, default 18) ──────────────────────
        // Computed in whole years at "now", not by subtracting years, so a client
        // born on 29 February is not rejected one day early.
        var minimumAge = await settings.GetIntAsync(tenantId, CustomerSettingKeys.MinimumAge, ct);
        if (CompletedYears(cmd.DateOfBirth, DateOnly.FromDateTime(now.UtcDateTime)) < minimumAge)
            return Result.Fail<CreateClientResult>(CustomerErrors.ClientUnderMinimumAge);

        // ── 2. Identity document: HARD block ─────────────────────────────────
        // Returned as a SUCCESSFUL result carrying the existing client, because the
        // operator's next move is to open that record — a bare error code would hide
        // which client to open. The endpoint answers 409.
        var documentNumber = cmd.IdentityDocumentNumber.Trim();
        var documentBlindIndex = indexer.Compute(BlindIndexPurpose.IdentityDocument, documentNumber);

        var documentHit = await duplicateProbe.FindByIdentityDocumentAsync(
            tenantId, documentBlindIndex, excludeClientId: null, ct);

        if (documentHit is not null)
        {
            return Result.Ok(new CreateClientResult(
                CreateClientOutcome.BlockedDuplicateIdentityDocument,
                ClientId: null,
                ClientNumber: null,
                Code: CustomerErrors.DuplicateIdentityDocument,
                Candidates: [documentHit]));
        }

        // ── 3. Phones: SOFT warning, overridable ─────────────────────────────
        // De-duplicated by blind index first: the same number typed twice (or typed
        // once as "+225 07…" and once as "07…") must not probe or store twice.
        var phones = DistinctPhones(cmd.PhoneNumbers);

        if (!cmd.ConfirmNoDuplicate)
        {
            // Probed only when the operator has NOT already acknowledged the warning:
            // once confirmed, the answer cannot change the outcome, so the queries
            // would be pure cost.
            var candidates = new List<DuplicateHit>();
            foreach (var phone in phones)
            {
                candidates.AddRange(
                    await duplicateProbe.FindActiveByPhoneAsync(
                        tenantId, phone.BlindIndex, excludeClientId: null, ct));
            }

            var distinctCandidates = candidates
                .GroupBy(c => c.ClientId)
                .Select(g => g.First())
                .ToList();

            if (distinctCandidates.Count > 0)
            {
                return Result.Ok(new CreateClientResult(
                    CreateClientOutcome.WarningPossibleDuplicatePhone,
                    ClientId: null,
                    ClientNumber: null,
                    Code: CustomerErrors.PossibleDuplicatePhone,
                    Candidates: distinctCandidates));
            }
        }

        // ── 4. Agency code ───────────────────────────────────────────────────
        // AgencyAuthorizationBehavior has already checked the caller's perimeter (the
        // command is IAgencyScopedRequest). This resolves the CODE, which the client
        // number is built from — and an agency the directory cannot resolve is, by
        // definition, not one this caller may book into.
        var agencyCode = await agencyDirectory.GetAgencyCodeAsync(tenantId, cmd.AgencyId, ct);
        if (string.IsNullOrWhiteSpace(agencyCode))
            return Result.Fail<CreateClientResult>(CustomerErrors.AgencyOutOfScope);

        // ── 5. Client number (tenant + agency + year sequence) ───────────────
        var clientNumber = await clientNumberGenerator.NextAsync(tenantId, agencyCode, ct);

        // ── 6. Protection of every regulated field ───────────────────────────
        // The date of birth is stored in ISO form so its ciphertext is stable and its
        // blind index matches whatever format the search endpoint later receives.
        var dateOfBirthIso = cmd.DateOfBirth.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var encryptedDateOfBirth = encryptor.Encrypt(dateOfBirthIso);
        var dateOfBirthBlindIndex = indexer.Compute(BlindIndexPurpose.DateOfBirth, dateOfBirthIso);

        // Invariant culture: a declared income written with a comma decimal separator
        // in one locale must decrypt to the same number in another.
        var encryptedDeclaredIncome = cmd.DeclaredIncome.HasValue
            ? encryptor.Encrypt(cmd.DeclaredIncome.Value.ToString(CultureInfo.InvariantCulture))
            : null;

        var encryptedDocumentNumber = encryptor.Encrypt(documentNumber);

        // ── 7. Aggregate ─────────────────────────────────────────────────────
        var client = Client.CreateIndividual(
            tenantId: tenantId,
            clientNumber: clientNumber,
            agencyId: cmd.AgencyId,
            agencyCode: agencyCode,
            advisorUserId: cmd.AdvisorUserId,
            firstName: cmd.FirstName,
            lastName: cmd.LastName,
            maidenName: cmd.MaidenName,
            gender: cmd.Gender,
            encryptedDateOfBirth: encryptedDateOfBirth,
            dateOfBirthBlindIndex: dateOfBirthBlindIndex,
            birthPlace: cmd.BirthPlace,
            nationality: cmd.Nationality,
            maritalStatus: cmd.MaritalStatus,
            fatherName: cmd.FatherName,
            motherName: cmd.MotherName,
            profession: cmd.Profession,
            employer: cmd.Employer,
            encryptedDeclaredIncome: encryptedDeclaredIncome,
            declaredIncomeCurrency: cmd.DeclaredIncomeCurrency,
            // Blank falls back to the module default inside the aggregate.
            preferredLanguage: cmd.PreferredLanguage ?? string.Empty,
            docType: cmd.IdentityDocumentType,
            encryptedDocNumber: encryptedDocumentNumber,
            docNumberBlindIndex: documentBlindIndex,
            docIssuedOn: cmd.IdentityDocumentIssuedOn,
            docExpiresOn: cmd.IdentityDocumentExpiresOn,
            createdBy: actor);

        // Surname first: the nightly duplicate detector BLOCKS on PhoneticKeyPrimary,
        // and in West-African records the surname is the stable half of a name.
        client.SetPhoneticKeys(
            phoneticKeys.Compute(cmd.LastName),
            phoneticKeys.Compute(cmd.FirstName));

        // ── 8. Contact points ────────────────────────────────────────────────
        var isFirstPhone = true;
        foreach (var phone in phones)
        {
            client.AddContactPoint(
                ContactPointType.Phone,
                EncryptRequired(phone.Clear),
                phone.BlindIndex,
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
                EncryptRequired(email),
                indexer.Compute(BlindIndexPurpose.Email, email),
                label: null,
                isPrimary: true,
                validFrom: now,
                actor: actor);
        }

        // The address is protected data too, so it is one encrypted contact point
        // rather than a set of clear columns — hence the canonical single line.
        var addressLine = cmd.Address?.ToSingleLineOrNull();
        if (addressLine is not null)
        {
            client.AddContactPoint(
                ContactPointType.Address,
                EncryptRequired(addressLine),
                indexer.Compute(BlindIndexPurpose.PostalAddress, addressLine),
                label: null,
                isPrimary: true,
                validFrom: now,
                actor: actor);
        }

        db.Clients.Add(client);

        // ── 9. Outbox BEFORE SaveChanges ─────────────────────────────────────
        // The publisher only stages an OutboxMessage; the single SaveChanges below
        // commits the client, its contact points and the event atomically. Publishing
        // after the commit would let a crash lose the event, leaving M02 without a KYC
        // file for an existing client.
        await publisher.PublishAsync(
            new ClientCreatedEvent(
                TenantId: tenantId,
                ClientId: client.Id,
                ClientNumber: client.ClientNumber,
                ClientType: client.Type.ToString(),
                AgencyId: client.AgencyId,
                AdvisorUserId: client.AdvisorUserId,
                SourceLeadId: client.SourceLeadId,
                CreatedBy: actor),
            ct);

        await db.SaveChangesAsync(ct);

        return Result.Ok(new CreateClientResult(
            CreateClientOutcome.Created,
            ClientId: client.Id,
            ClientNumber: client.ClientNumber,
            Code: null,
            Candidates: []));
    }

    /// <summary>
    /// Whole years elapsed between two dates — the legal definition of age, as opposed
    /// to a day count divided by 365.
    /// </summary>
    private static int CompletedYears(DateOnly birthDate, DateOnly on)
    {
        var years = on.Year - birthDate.Year;
        if (birthDate > on.AddYears(-years)) years--;
        return years;
    }

    /// <summary>
    /// Trimmed phone numbers paired with their blind index, de-duplicated ON THE INDEX:
    /// two spellings of the same number normalize to one index, so the client ends up
    /// with one contact point and one probe instead of two.
    /// </summary>
    private List<(string Clear, string BlindIndex)> DistinctPhones(IReadOnlyList<string>? raw)
    {
        var result = new List<(string Clear, string BlindIndex)>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var value in raw ?? [])
        {
            if (string.IsNullOrWhiteSpace(value)) continue;

            var clear = value.Trim();
            var blindIndex = indexer.Compute(BlindIndexPurpose.Phone, clear);

            if (seen.Add(blindIndex))
                result.Add((clear, blindIndex));
        }

        return result;
    }

    /// <summary>
    /// <see cref="IFieldEncryptor.Encrypt"/> is null-in / null-out; every call site here
    /// passes a non-blank value, so a null back means the encryptor is misconfigured —
    /// an infrastructure fault, not a business outcome, hence an exception.
    /// </summary>
    private string EncryptRequired(string clear)
        => encryptor.Encrypt(clear)
           ?? throw new InvalidOperationException(
               "Field encryption returned null for a non-null value; check Customers:FieldEncryptionKey.");
}
