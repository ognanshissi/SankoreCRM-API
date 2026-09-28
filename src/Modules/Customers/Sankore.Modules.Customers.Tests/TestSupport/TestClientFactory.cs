namespace Sankore.Modules.Customers.Tests.TestSupport;

using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Customers.Domain;
using Sankore.Modules.Customers.Infrastructure;
using Sankore.Shared.Infrastructure.Crypto;

/// <summary>
/// Builders for the aggregates M01 tests need over and over.
///
/// Every parameter has a West-African-realistic default so a test that does not care
/// about identity can write <c>TestClientFactory.Individual(tenantId, agencyId)</c> and
/// keep its assertions on the one thing it is actually about. Sensitive values go
/// through the same <see cref="TestDoubles.Encryptor()"/> / <see cref="TestDoubles.Indexer()"/>
/// fakes as the handlers, so a client built here is indistinguishable from one a
/// handler would have produced.
/// </summary>
public static class TestClientFactory
{
    private static readonly IFieldEncryptor Encryptor = TestDoubles.Encryptor();
    private static readonly IBlindIndexer Indexer = TestDoubles.Indexer();

    /// <summary>Default date of birth: comfortably above any tenant's minimum age.</summary>
    private static readonly DateOnly DefaultDateOfBirth = new(1990, 4, 12);

    public static Client Individual(
        Guid tenantId,
        Guid agencyId,
        string first = "Awa",
        string last = "Ouattara",
        string clientNumber = "ABJ-2026-000001",
        string agencyCode = "ABJ",
        Guid? advisorUserId = null,
        Gender gender = Gender.Female,
        DateOnly? dateOfBirth = null,
        string? identityDocumentNumber = null,
        IdentityDocumentType? identityDocumentType = null,
        string nationality = "CI",
        string preferredLanguage = "fr",
        Guid? createdBy = null,
        Guid? sourceLeadId = null)
    {
        var dob = dateOfBirth ?? DefaultDateOfBirth;
        var dobText = dob.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

        // A document number implies a type, and vice versa: keep the pair coherent so
        // tests never produce a client the real validators would have rejected.
        var docType = identityDocumentType
            ?? (identityDocumentNumber is null ? null : IdentityDocumentType.NationalIdCard);

        return Client.CreateIndividual(
            tenantId: tenantId,
            clientNumber: clientNumber,
            agencyId: agencyId,
            agencyCode: agencyCode,
            advisorUserId: advisorUserId,
            firstName: first,
            lastName: last,
            maidenName: null,
            gender: gender,
            encryptedDateOfBirth: Encryptor.Encrypt(dobText),
            dateOfBirthBlindIndex: Indexer.Compute(BlindIndexPurpose.DateOfBirth, dobText),
            birthPlace: "Abidjan",
            nationality: nationality,
            maritalStatus: MaritalStatus.Single,
            fatherName: null,
            motherName: null,
            profession: "Commerçante",
            employer: null,
            encryptedDeclaredIncome: null,
            declaredIncomeCurrency: null,
            preferredLanguage: preferredLanguage,
            docType: docType,
            encryptedDocNumber: Encryptor.Encrypt(identityDocumentNumber),
            docNumberBlindIndex: identityDocumentNumber is null
                ? null
                : Indexer.Compute(BlindIndexPurpose.IdentityDocument, identityDocumentNumber),
            docIssuedOn: identityDocumentNumber is null ? null : new DateOnly(2022, 1, 10),
            docExpiresOn: identityDocumentNumber is null ? null : new DateOnly(2032, 1, 9),
            createdBy: createdBy ?? Guid.NewGuid(),
            sourceLeadId: sourceLeadId);
    }

    public static Client Legal(
        Guid tenantId,
        Guid agencyId,
        string legalName = "Coopérative Djigui",
        string legalFormCode = "SCOOPS",
        string clientNumber = "ABJ-2026-000002",
        string agencyCode = "ABJ",
        Guid? advisorUserId = null,
        string? registrationNumber = null,
        string preferredLanguage = "fr",
        Guid? createdBy = null,
        Guid? sourceLeadId = null)
        => Client.CreateLegal(
            tenantId: tenantId,
            clientNumber: clientNumber,
            agencyId: agencyId,
            agencyCode: agencyCode,
            advisorUserId: advisorUserId,
            legalName: legalName,
            legalFormCode: legalFormCode,
            encryptedRegistrationNumber: Encryptor.Encrypt(registrationNumber),
            registrationNumberBlindIndex: registrationNumber is null
                ? null
                : Indexer.Compute(BlindIndexPurpose.RegistrationNumber, registrationNumber),
            encryptedTaxIdNumber: null,
            incorporationDate: new DateOnly(2019, 6, 1),
            preferredLanguage: preferredLanguage,
            createdBy: createdBy ?? Guid.NewGuid(),
            sourceLeadId: sourceLeadId);

    public static ClientGroup Group(
        Guid tenantId,
        Guid agencyId,
        GroupType type = GroupType.SolidarityGroup,
        string name = "Groupe Nimba",
        DateOnly? constitutionDate = null,
        Guid? createdBy = null)
        => ClientGroup.Create(
            tenantId: tenantId,
            type: type,
            name: name,
            agencyId: agencyId,
            constitutionDate: constitutionDate ?? new DateOnly(2026, 1, 15),
            createdBy: createdBy ?? Guid.NewGuid());

    /// <summary>
    /// Builds an individual and commits it, returning the tracked instance. Use this
    /// when the test needs the row to exist before the handler runs.
    /// </summary>
    public static async Task<Client> SeedIndividualAsync(
        CustomersDbContext db,
        Guid tenantId,
        Guid agencyId,
        string first = "Awa",
        string last = "Ouattara",
        string clientNumber = "ABJ-2026-000001",
        string agencyCode = "ABJ",
        Guid? advisorUserId = null,
        string? identityDocumentNumber = null,
        Guid? sourceLeadId = null,
        CancellationToken ct = default)
    {
        var client = Individual(
            tenantId, agencyId, first, last, clientNumber, agencyCode,
            advisorUserId: advisorUserId,
            identityDocumentNumber: identityDocumentNumber,
            sourceLeadId: sourceLeadId);

        return await SeedAsync(db, client, ct);
    }

    /// <summary>Commits an already-built client (useful after calling domain methods on it).</summary>
    public static async Task<Client> SeedAsync(CustomersDbContext db, Client client, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(client);

        db.Clients.Add(client);
        await db.SaveChangesAsync(ct);
        // Detach so the next context/query in the test reads from the store rather than
        // from this context's identity map — the same thing a new request would do.
        db.ChangeTracker.Clear();
        return client;
    }

    public static async Task<ClientGroup> SeedGroupAsync(
        CustomersDbContext db,
        ClientGroup group,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(group);

        db.ClientGroups.Add(group);
        await db.SaveChangesAsync(ct);
        db.ChangeTracker.Clear();
        return group;
    }
}
