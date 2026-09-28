namespace Sankore.Modules.Customers.Tests.Features.LegalEntities;

using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Customers.Domain;
using Sankore.Modules.Customers.Features.Clients.Shared;
using Sankore.Modules.Customers.Features.LegalEntities.CreateLegalClient;
using Sankore.Modules.Customers.PublicApi.Events;
using Sankore.Modules.Customers.Tests.TestSupport;
using Sankore.Shared.Infrastructure.Crypto;
using Xunit;

/// <summary>US-M01-BE-17 — registration of a legal entity.</summary>
public sealed class CreateLegalClientHandlerTests : IDisposable
{
    private static readonly Guid TenantId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid OtherTenantId = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid AgencyId = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001");
    private static readonly Guid UserId = Guid.Parse("bbbbbbbb-0000-0000-0000-000000000001");

    private const string Rccm = "CI-ABJ-2019-B-12345";

    private readonly TestCustomersDbContextFactory _factory = new(TenantId);
    private readonly IFieldEncryptor _encryptor = TestDoubles.Encryptor();
    private readonly IBlindIndexer _indexer = TestDoubles.Indexer();

    public void Dispose() => _factory.Dispose();

    private CreateLegalClientHandler BuildHandler(
        out RecordingEventPublisher publisher,
        string? agencyCode = LegalEntitiesTestDoubles.AgencyCode)
    {
        publisher = new RecordingEventPublisher();
        var db = _factory.CreateContext();

        return new CreateLegalClientHandler(
            db,
            TestDoubles.CurrentUser(TenantId, UserId),
            // The REAL probe: the duplicate check under test is its blind-index equality.
            new DuplicateProbe(db),
            LegalEntitiesTestDoubles.AgencyDirectory(agencyCode),
            LegalEntitiesTestDoubles.ClientNumbers(),
            _encryptor,
            _indexer,
            LegalEntitiesTestDoubles.PhoneticKeys("SNKR"),
            publisher);
    }

    private static CreateLegalClientCommand Command(
        string registrationNumber = Rccm,
        string legalFormCode = "SARL",
        string? taxIdNumber = "CI1234567890") =>
        new(
            AgencyId: AgencyId,
            AdvisorUserId: null,
            LegalName: "Sankore Distribution",
            LegalFormCode: legalFormCode,
            RegistrationNumber: registrationNumber,
            TaxIdNumber: taxIdNumber,
            IncorporationDate: new DateOnly(2019, 4, 12),
            HeadOfficeAddress: new PostalAddressInput("Rue des Jardins", "Abidjan", "Cocody", "CI", "01BP1234"),
            PhoneNumbers: ["+2250708091810"],
            Email: "contact@sankore.ci",
            PreferredLanguage: "fr");

    private async Task SeedLegalFormsAsync(Guid tenantId)
    {
        await using var seed = _factory.CreateContext();
        await LegalEntitiesTestDoubles.SeedLegalFormsAsync(seed, tenantId, "SARL", "SA");
    }

    [Fact]
    public async Task Creates_a_legal_client_pending_kyc_with_a_client_number_and_publishes_the_creation_event()
    {
        await SeedLegalFormsAsync(TenantId);
        var handler = BuildHandler(out var publisher);

        var result = await handler.Handle(Command(), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Outcome.Should().Be(CreateLegalClientOutcome.Created);
        result.Value.Code.Should().BeNull();
        result.Value.ClientNumber.Should().Be(LegalEntitiesTestDoubles.ClientNumber);

        publisher.OfType<ClientCreatedEvent>().Should().ContainSingle()
            .Which.Should().Match<ClientCreatedEvent>(e =>
                e.TenantId == TenantId
                && e.ClientId == result.Value.ClientId!.Value
                && e.ClientType == nameof(ClientType.Legal)
                && e.AgencyId == AgencyId
                && e.SourceLeadId == null
                && e.CreatedBy == UserId);

        await using var assertions = _factory.CreateContext();
        var stored = await assertions.Clients
            .Include(c => c.ContactPoints)
            .SingleAsync(c => c.Id == result.Value.ClientId!.Value);

        stored.Type.Should().Be(ClientType.Legal);
        stored.Status.Should().Be(ClientStatus.PendingKyc);
        stored.LegalName.Should().Be("Sankore Distribution");
        stored.LegalFormCode.Should().Be("SARL");
        stored.IncorporationDate.Should().Be(new DateOnly(2019, 4, 12));
        stored.PhoneticKeyPrimary.Should().Be("SNKR");
        // A company has a single name: no secondary phonetic key to compute.
        stored.PhoneticKeySecondary.Should().BeNull();

        // One phone (primary), one e-mail, one head-office address.
        stored.ContactPoints.Should().HaveCount(3);
        stored.ContactPoints.Single(cp => cp.Type == ContactPointType.Phone).IsPrimary.Should().BeTrue();
        stored.ContactPoints.Should().ContainSingle(cp => cp.Type == ContactPointType.Address)
            .Which.Label.Should().Be("HeadOffice");
        stored.ContactPoints.Should().OnlyContain(cp => cp.BlindIndex.Length > 0);
    }

    [Fact]
    public async Task Stores_the_registration_number_and_the_tax_id_encrypted_with_a_blind_index_on_the_rccm()
    {
        await SeedLegalFormsAsync(TenantId);
        var handler = BuildHandler(out _);

        var result = await handler.Handle(Command(), CancellationToken.None);

        await using var assertions = _factory.CreateContext();
        var stored = await assertions.Clients.SingleAsync(c => c.Id == result.Value.ClientId!.Value);

        // Nothing readable at rest…
        stored.EncryptedRegistrationNumber.Should().NotBeNullOrEmpty().And.NotBe(Rccm);
        stored.EncryptedTaxIdNumber.Should().NotBeNullOrEmpty().And.NotBe("CI1234567890");

        // …and the values ARE recoverable, so the encryption is real and not a hash.
        _encryptor.Decrypt(stored.EncryptedRegistrationNumber).Should().Be(Rccm);
        _encryptor.Decrypt(stored.EncryptedTaxIdNumber).Should().Be("CI1234567890");

        // The blind index is what makes the duplicate check possible without decrypting.
        stored.RegistrationNumberBlindIndex.Should()
            .Be(_indexer.Compute(BlindIndexPurpose.RegistrationNumber, Rccm));
    }

    [Fact]
    public async Task Blocks_a_registration_number_already_used_in_the_tenant_and_returns_the_existing_client()
    {
        await SeedLegalFormsAsync(TenantId);

        var blindIndex = _indexer.Compute(BlindIndexPurpose.RegistrationNumber, Rccm);
        var incumbent = LegalEntitiesTestDoubles.LegalClient(
            TenantId, AgencyId, UserId,
            encryptedRegistrationNumber: _encryptor.Encrypt(Rccm),
            registrationNumberBlindIndex: blindIndex,
            clientNumber: "AG000001-2026-000001");

        await using (var seed = _factory.CreateContext())
        {
            seed.Clients.Add(incumbent);
            await seed.SaveChangesAsync();
        }

        var handler = BuildHandler(out var publisher);

        var result = await handler.Handle(Command(), CancellationToken.None);

        // A blocking duplicate is a SUCCESSFUL result carrying a code — that is what lets
        // the endpoint answer 409 together with the offending company.
        result.IsSuccess.Should().BeTrue();
        result.Value.Outcome.Should().Be(CreateLegalClientOutcome.BlockedDuplicateRegistrationNumber);
        result.Value.Code.Should().Be(CustomerErrors.DuplicateRegistrationNumber);
        result.Value.ClientId.Should().BeNull();
        result.Value.Candidates.Should().ContainSingle()
            .Which.Should().Match<DuplicateHit>(h =>
                h.ClientId == incumbent.Id && h.ClientNumber == "AG000001-2026-000001");

        publisher.Published.Should().BeEmpty();

        await using var assertions = _factory.CreateContext();
        (await assertions.Clients.CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task Rejects_a_legal_form_missing_from_the_tenants_active_list()
    {
        await SeedLegalFormsAsync(TenantId);
        var handler = BuildHandler(out _);

        var result = await handler.Handle(Command(legalFormCode: "LLC"), CancellationToken.None);

        result.Error.Should().Be(CustomerErrors.LegalFormUnknown);
    }

    [Fact]
    public async Task Rejects_a_legal_form_the_tenant_has_deactivated()
    {
        await using (var seed = _factory.CreateContext())
        {
            var retired = LegalForm.Create(TenantId, "GIE", "Groupement d'intérêt économique", 5);
            retired.Deactivate();
            seed.LegalForms.Add(retired);
            await seed.SaveChangesAsync();
        }

        var handler = BuildHandler(out _);

        var result = await handler.Handle(Command(legalFormCode: "GIE"), CancellationToken.None);

        result.Error.Should().Be(CustomerErrors.LegalFormUnknown);
    }

    [Fact]
    public async Task Rejects_an_agency_the_directory_cannot_resolve()
    {
        await SeedLegalFormsAsync(TenantId);
        var handler = BuildHandler(out _, agencyCode: null);

        var result = await handler.Handle(Command(), CancellationToken.None);

        result.Error.Should().Be(CustomerErrors.AgencyOutOfScope);
    }

    [Fact]
    public async Task Does_not_see_the_registration_number_of_another_tenant()
    {
        // Same shared in-memory store, same RCCM, another tenant: the probe is scoped by
        // tenant, so this is a legitimate creation and not a duplicate.
        await SeedLegalFormsAsync(TenantId);

        var foreign = LegalEntitiesTestDoubles.LegalClient(
            OtherTenantId, AgencyId, UserId,
            encryptedRegistrationNumber: _encryptor.Encrypt(Rccm),
            registrationNumberBlindIndex: _indexer.Compute(BlindIndexPurpose.RegistrationNumber, Rccm),
            clientNumber: "AG000001-2026-000001");

        await using (var seed = _factory.CreateContext())
        {
            seed.Clients.Add(foreign);
            await seed.SaveChangesAsync();
        }

        var handler = BuildHandler(out _);

        var result = await handler.Handle(Command(), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Outcome.Should().Be(CreateLegalClientOutcome.Created);

        await using var assertions = _factory.CreateContext();
        // The ambient tenant filter hides the foreign row entirely.
        var visible = await assertions.Clients.ToListAsync();
        visible.Should().ContainSingle().Which.TenantId.Should().Be(TenantId);
    }

    [Fact]
    public async Task Does_not_reuse_a_legal_form_declared_by_another_tenant()
    {
        // The legal-form list is tenant configuration: tenant A cannot ride on tenant B's.
        await SeedLegalFormsAsync(OtherTenantId);
        var handler = BuildHandler(out _);

        var result = await handler.Handle(Command(), CancellationToken.None);

        result.Error.Should().Be(CustomerErrors.LegalFormUnknown);
    }
}
