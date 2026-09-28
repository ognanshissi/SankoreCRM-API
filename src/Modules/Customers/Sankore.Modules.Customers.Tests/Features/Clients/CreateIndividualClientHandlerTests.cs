namespace Sankore.Modules.Customers.Tests.Features.Clients;

using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Customers.Domain;
using Sankore.Modules.Customers.Features.Clients.CreateIndividualClient;
using Sankore.Modules.Customers.Features.Clients.Shared;
using Sankore.Modules.Customers.Infrastructure;
using Sankore.Modules.Customers.PublicApi.Events;
using Sankore.Modules.Customers.Tests.TestSupport;
using Sankore.Shared.Infrastructure.Crypto;
using Sankore.Shared.Infrastructure.Outbox;
using Sankore.Shared.Kernel;
using Xunit;

public sealed class CreateIndividualClientHandlerTests : IDisposable
{
    private static readonly Guid TenantId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid AgencyId = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid ActorId = Guid.Parse("33333333-3333-3333-3333-333333333333");

    private const string DocumentNumber = "CI0012345642";
    private const string PhoneNumber = "+225 07 08 09 18";

    private readonly TestCustomersDbContextFactory _factory = new(TenantId);

    public void Dispose() => _factory.Dispose();

    [Fact]
    public async Task Creates_the_client_in_pending_kyc_with_a_minted_number_and_an_outbox_event()
    {
        await using var db = _factory.CreateContext();
        var handler = BuildHandler(db, out _);

        var result = await handler.Handle(Command(), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Outcome.Should().Be(CreateClientOutcome.Created);
        result.Value.ClientNumber.Should().Be(ClientsTestHarness.MintedClientNumber);
        result.Value.Code.Should().BeNull();
        result.Value.Candidates.Should().BeEmpty();

        await using var read = _factory.CreateContext();
        var stored = await read.Clients
            .Include(c => c.ContactPoints)
            .Include(c => c.StatusHistory)
            .SingleAsync(c => c.Id == result.Value.ClientId!.Value);

        stored.Status.Should().Be(ClientStatus.PendingKyc);
        stored.Type.Should().Be(ClientType.Individual);
        stored.AgencyCode.Should().Be(ClientsTestHarness.AgencyCode);
        stored.ClientNumber.Should().Be(ClientsTestHarness.MintedClientNumber);
        stored.CreatedBy.Should().Be(ActorId);
        stored.StatusHistory.Should().ContainSingle()
            .Which.NewStatus.Should().Be(ClientStatus.PendingKyc);

        // The event must be in the SAME committed state as the client: the handler stages
        // it on the outbox before its single SaveChanges.
        var outbox = await read.Set<OutboxMessage>().SingleAsync();
        outbox.EventType.Should().Contain(nameof(ClientCreatedEvent));
        outbox.PayloadJson.Should().Contain(result.Value.ClientId!.Value.ToString("D"));
    }

    [Fact]
    public async Task Stores_every_protected_value_encrypted_and_fills_the_blind_indexes()
    {
        await using var db = _factory.CreateContext();
        var handler = BuildHandler(db, out _);

        var result = await handler.Handle(
            Command(email: "awa@example.com", street: "12 rue des Jardins"), CancellationToken.None);

        await using var read = _factory.CreateContext();
        var stored = await read.Clients
            .Include(c => c.ContactPoints)
            .SingleAsync(c => c.Id == result.Value.ClientId!.Value);

        stored.EncryptedIdentityDocumentNumber.Should().NotBeNullOrWhiteSpace();
        stored.EncryptedIdentityDocumentNumber.Should().NotBe(DocumentNumber);
        stored.IdentityDocumentNumberBlindIndex.Should().NotBeNullOrWhiteSpace();

        stored.EncryptedDateOfBirth.Should().NotBeNullOrWhiteSpace();
        stored.DateOfBirthBlindIndex.Should().NotBeNullOrWhiteSpace();

        stored.EncryptedDeclaredIncome.Should().NotBeNullOrWhiteSpace();
        stored.EncryptedDeclaredIncome.Should().NotBe("450000");

        // The phonetic keys are what the nightly duplicate detector blocks on.
        stored.PhoneticKeyPrimary.Should().NotBeNullOrWhiteSpace();

        stored.ContactPoints.Should().HaveCount(3);
        foreach (var contactPoint in stored.ContactPoints)
        {
            contactPoint.EncryptedValue.Should().NotBeNullOrWhiteSpace();
            contactPoint.BlindIndex.Should().NotBeNullOrWhiteSpace();
            contactPoint.IsActive.Should().BeTrue();
        }

        stored.ContactPoints.Single(cp => cp.Type == ContactPointType.Phone)
            .EncryptedValue.Should().NotBe(PhoneNumber);
        stored.ContactPoints.Single(cp => cp.Type == ContactPointType.Phone)
            .IsPrimary.Should().BeTrue();
    }

    [Fact]
    public async Task Marks_only_the_first_phone_as_primary()
    {
        await using var db = _factory.CreateContext();
        var handler = BuildHandler(db, out _);

        var command = Command() with { PhoneNumbers = [PhoneNumber, "07 01 02 03 04"] };

        var result = await handler.Handle(command, CancellationToken.None);

        await using var read = _factory.CreateContext();
        var phones = await read.ClientContactPoints
            .Where(cp => cp.ClientId == result.Value.ClientId!.Value && cp.Type == ContactPointType.Phone)
            .ToListAsync();

        phones.Should().HaveCount(2);
        phones.Count(p => p.IsPrimary).Should().Be(1);
    }

    [Fact]
    public async Task Collapses_two_spellings_of_the_same_phone_into_one_contact_point()
    {
        await using var db = _factory.CreateContext();
        var handler = BuildHandler(db, out _);

        // Same subscriber, written once with a '+' indicatif and once with the "00"
        // international prefix, with different spacing. SensitiveValueNormalizer folds both
        // to the same value, so the blind index collides and only ONE contact point may be
        // created — otherwise every client would accumulate duplicate rows for one number.
        var command = Command() with { PhoneNumbers = ["+225 07 08 09 18", "00225-0708.0918"] };

        var result = await handler.Handle(command, CancellationToken.None);

        await using var read = _factory.CreateContext();
        var phones = await read.ClientContactPoints
            .Where(cp => cp.ClientId == result.Value.ClientId!.Value && cp.Type == ContactPointType.Phone)
            .ToListAsync();

        phones.Should().ContainSingle();
    }

    [Fact]
    public async Task Rejects_an_applicant_below_the_tenant_minimum_age()
    {
        await using var db = _factory.CreateContext();
        var handler = BuildHandler(db, out _);

        var sixteenYearsOld = DateOnly.FromDateTime(DateTimeOffset.UtcNow.UtcDateTime).AddYears(-16);

        var result = await handler.Handle(
            Command() with { DateOfBirth = sixteenYearsOld }, CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Be(CustomerErrors.ClientUnderMinimumAge);

        await using var read = _factory.CreateContext();
        (await read.Clients.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task Accepts_an_applicant_who_reaches_the_minimum_age_exactly_today()
    {
        await using var db = _factory.CreateContext();
        var handler = BuildHandler(db, out _);

        var eighteenToday = DateOnly.FromDateTime(DateTimeOffset.UtcNow.UtcDateTime).AddYears(-18);

        var result = await handler.Handle(
            Command() with { DateOfBirth = eighteenToday }, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Outcome.Should().Be(CreateClientOutcome.Created);
    }

    [Fact]
    public async Task Blocks_a_duplicate_identity_document_and_returns_the_existing_client_number()
    {
        await using var seed = _factory.CreateContext();
        await TestClientFactory.SeedIndividualAsync(
            seed, TenantId, AgencyId,
            first: "Awa", last: "Kone",
            clientNumber: "ABJ-2026-000001",
            identityDocumentNumber: DocumentNumber);

        await using var db = _factory.CreateContext();
        var handler = BuildHandler(db, out _);

        var result = await handler.Handle(Command(), CancellationToken.None);

        // A block is a SUCCESSFUL result carrying the candidate: the operator's next move
        // is to open that record, which a bare error code could not point at.
        result.IsSuccess.Should().BeTrue();
        result.Value.Outcome.Should().Be(CreateClientOutcome.BlockedDuplicateIdentityDocument);
        result.Value.Code.Should().Be(CustomerErrors.DuplicateIdentityDocument);
        result.Value.ClientId.Should().BeNull();
        result.Value.Candidates.Should().ContainSingle()
            .Which.ClientNumber.Should().Be("ABJ-2026-000001");

        await using var read = _factory.CreateContext();
        (await read.Clients.CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task Blocks_a_duplicate_identity_document_even_when_the_operator_confirmed_no_duplicate()
    {
        await using var seed = _factory.CreateContext();
        await TestClientFactory.SeedIndividualAsync(
            seed, TenantId, AgencyId, identityDocumentNumber: DocumentNumber);

        await using var db = _factory.CreateContext();
        var handler = BuildHandler(db, out _);

        var result = await handler.Handle(
            Command() with { ConfirmNoDuplicate = true }, CancellationToken.None);

        // confirmNoDuplicate overrides the phone WARNING only. Two people holding one
        // national id card is never acceptable.
        result.Value.Outcome.Should().Be(CreateClientOutcome.BlockedDuplicateIdentityDocument);
    }

    [Fact]
    public async Task Warns_on_a_possible_duplicate_phone_and_returns_the_candidates()
    {
        await using var seed = _factory.CreateContext();
        var existing = TestClientFactory.Individual(
            TenantId, AgencyId, first: "Ibrahim", last: "Kone", clientNumber: "ABJ-2026-000007");

        existing.AddContactPoint(
            ContactPointType.Phone,
            TestDoubles.Encryptor().Encrypt(PhoneNumber)!,
            TestDoubles.Indexer().Compute(BlindIndexPurpose.Phone, PhoneNumber),
            label: null,
            isPrimary: true,
            validFrom: DateTimeOffset.UtcNow.AddYears(-1),
            actor: ActorId);

        await TestClientFactory.SeedAsync(seed, existing);

        await using var db = _factory.CreateContext();
        var handler = BuildHandler(db, out _);

        var result = await handler.Handle(Command(), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Outcome.Should().Be(CreateClientOutcome.WarningPossibleDuplicatePhone);
        result.Value.Code.Should().Be(CustomerErrors.PossibleDuplicatePhone);
        result.Value.Candidates.Should().ContainSingle()
            .Which.ClientNumber.Should().Be("ABJ-2026-000007");

        await using var read = _factory.CreateContext();
        (await read.Clients.CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task Creates_the_client_when_the_operator_confirms_the_phone_is_shared()
    {
        await using var seed = _factory.CreateContext();
        var existing = TestClientFactory.Individual(
            TenantId, AgencyId, first: "Ibrahim", last: "Kone", clientNumber: "ABJ-2026-000007");

        existing.AddContactPoint(
            ContactPointType.Phone,
            TestDoubles.Encryptor().Encrypt(PhoneNumber)!,
            TestDoubles.Indexer().Compute(BlindIndexPurpose.Phone, PhoneNumber),
            label: null,
            isPrimary: true,
            validFrom: DateTimeOffset.UtcNow.AddYears(-1),
            actor: ActorId);

        await TestClientFactory.SeedAsync(seed, existing);

        await using var db = _factory.CreateContext();
        var handler = BuildHandler(db, out _);

        var result = await handler.Handle(
            Command() with { ConfirmNoDuplicate = true }, CancellationToken.None);

        result.Value.Outcome.Should().Be(CreateClientOutcome.Created);

        await using var read = _factory.CreateContext();
        (await read.Clients.CountAsync()).Should().Be(2);
    }

    [Fact]
    public async Task Never_decrypts_anything_while_checking_for_duplicates()
    {
        await using var seed = _factory.CreateContext();
        await TestClientFactory.SeedIndividualAsync(
            seed, TenantId, AgencyId, identityDocumentNumber: DocumentNumber);

        await using var db = _factory.CreateContext();
        var handler = BuildHandler(db, out var encryptor);

        var result = await handler.Handle(Command(), CancellationToken.None);

        result.Value.Outcome.Should().Be(CreateClientOutcome.BlockedDuplicateIdentityDocument);

        // The whole point of the blind index: the duplicate is found by comparing HMACs,
        // so not a single protected column is unwrapped. A regression here would mean the
        // probe started loading and decrypting candidate rows.
        encryptor.DecryptCalls.Should().Be(0);
    }

    [Fact]
    public async Task Rejects_an_agency_the_directory_cannot_resolve()
    {
        await using var db = _factory.CreateContext();
        var handler = BuildHandler(db, out _);

        var result = await handler.Handle(
            Command() with { AgencyId = Guid.NewGuid() }, CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Be(CustomerErrors.AgencyOutOfScope);
    }

    // ─────────────────────────────────────────────────────────────────────────

    private static CreateIndividualClientCommand Command(
        string? email = null,
        string? street = null)
        => new(
            AgencyId: AgencyId,
            AdvisorUserId: null,
            FirstName: "Awa",
            LastName: "Traoré",
            MaidenName: null,
            Gender: Gender.Female,
            DateOfBirth: new DateOnly(1990, 4, 12),
            BirthPlace: "Abidjan",
            Nationality: "CI",
            MaritalStatus: MaritalStatus.Single,
            FatherName: null,
            MotherName: null,
            Profession: "Commerçante",
            Employer: null,
            DeclaredIncome: 450000m,
            DeclaredIncomeCurrency: "XOF",
            PreferredLanguage: "fr",
            IdentityDocumentType: IdentityDocumentType.NationalIdCard,
            IdentityDocumentNumber: DocumentNumber,
            IdentityDocumentIssuedOn: new DateOnly(2022, 1, 10),
            IdentityDocumentExpiresOn: new DateOnly(2032, 1, 9),
            PhoneNumbers: [PhoneNumber],
            Email: email,
            Address: street is null ? null : new PostalAddressInput(street, "Abidjan", "Lagunes", "CI", "01"),
            ConfirmNoDuplicate: false);

    private static CreateIndividualClientHandler BuildHandler(
        CustomersDbContext db,
        out ClientsTestHarness.CountingFieldEncryptor encryptor)
    {
        encryptor = new ClientsTestHarness.CountingFieldEncryptor(TestDoubles.Encryptor());

        return new CreateIndividualClientHandler(
            db,
            TestDoubles.CurrentUser(TenantId, ActorId),
            TestDoubles.Settings(TenantId),
            new DuplicateProbe(db),
            ClientsTestHarness.AgencyDirectory(TenantId, AgencyId),
            ClientsTestHarness.ClientNumbers(),
            encryptor,
            TestDoubles.Indexer(),
            ClientsTestHarness.PhoneticKeys(),
            new OutboxEventPublisher<CustomersDbContext>(db));
    }
}
