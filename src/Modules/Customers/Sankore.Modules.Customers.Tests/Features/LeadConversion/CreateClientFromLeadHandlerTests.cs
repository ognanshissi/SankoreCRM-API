namespace Sankore.Modules.Customers.Tests.Features.LeadConversion;

using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Customers.Domain;
using Sankore.Modules.Customers.Features.Clients.Shared;
using Sankore.Modules.Customers.Features.LeadConversion.CreateClientFromLead;
using Sankore.Modules.Customers.PublicApi;
using Sankore.Modules.Customers.PublicApi.Events;
using Sankore.Modules.Customers.Tests.TestSupport;
using Sankore.Shared.Infrastructure.Crypto;
using Xunit;

/// <summary>
/// US-M01-BE-06 — lead → client conversion.
///
/// The handler is exercised directly (no MediatR pipeline): the pipeline's own behaviors have
/// their own tests, and what matters here is the conversion policy — idempotence on the source
/// lead, the advisor parameter, the blocking duplicate, the encryption of the clear-text values
/// that arrive from module M13, and tenant isolation on a code path that has no ambient tenant.
/// </summary>
public sealed class CreateClientFromLeadHandlerTests : IDisposable
{
    private static readonly Guid TenantId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid OtherTenantId = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid AgencyId = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001");
    private static readonly Guid ConvertingAgentId = Guid.Parse("bbbbbbbb-0000-0000-0000-000000000001");
    private static readonly Guid LeadId = Guid.Parse("cccccccc-0000-0000-0000-000000000001");

    private const string DocumentNumber = "CI-0123456789";
    private const string PhoneNumber = "+2250708091810";
    private const string Email = "awa.kone@example.ci";
    private static readonly DateOnly BirthDate = new(1990, 3, 17);
    private const string BirthDateIso = "1990-03-17";

    private readonly TestCustomersDbContextFactory _factory = new(TenantId);
    private readonly IBlindIndexer _indexer = TestDoubles.Indexer();

    public void Dispose() => _factory.Dispose();

    // ── Fixtures ────────────────────────────────────────────────────────────

    private CreateClientFromLeadHandler BuildHandler(
        out RecordingEventPublisher publisher,
        out CountingFieldEncryptor encryptor,
        bool advisorFromConvertingAgent = true,
        string? agencyCode = LeadConversionTestDoubles.AgencyCode)
    {
        publisher = new RecordingEventPublisher();
        encryptor = new CountingFieldEncryptor(TestDoubles.Encryptor());

        var db = _factory.CreateContext();

        return new CreateClientFromLeadHandler(
            db,
            // The REAL probe: the duplicate check under test IS its blind-index equality.
            new DuplicateProbe(db),
            LeadConversionTestDoubles.AgencyDirectory(agencyCode),
            LeadConversionTestDoubles.ClientNumbers(),
            TestDoubles.Settings(
                TenantId,
                (CustomerSettingKeys.AdvisorFromConvertingAgent,
                    advisorFromConvertingAgent ? "true" : "false")),
            encryptor,
            _indexer,
            LeadConversionTestDoubles.PhoneticKeys(),
            publisher);
    }

    private static CreateClientFromLeadCommand Command(
        Guid? tenantId = null,
        Guid? leadId = null,
        string? firstName = "Awa",
        string? lastName = "Kone",
        string? legalName = null,
        string? documentNumber = DocumentNumber,
        Guid? requestedClientId = null) =>
        new(
            TenantId: tenantId ?? TenantId,
            LeadId: leadId ?? LeadId,
            AgencyId: AgencyId,
            ConvertedByUserId: ConvertingAgentId,
            FirstName: firstName,
            LastName: lastName,
            LegalName: legalName,
            Gender: "Female",
            DateOfBirth: BirthDateIso,
            Nationality: "CI",
            PhoneNumber: PhoneNumber,
            Email: Email,
            IdentityDocumentType: "NationalIdCard",
            IdentityDocumentNumber: documentNumber,
            Profession: "Commerçante",
            PreferredLanguage: "fr",
            RequestedClientId: requestedClientId);

    /// <summary>Reads a client back on a fresh context, bypassing the ambient tenant filter.</summary>
    private async Task<Client?> LoadAsync(Guid clientId)
    {
        await using var db = _factory.CreateContext();
        return await db.Clients
            .IgnoreQueryFilters()
            .Include(c => c.ContactPoints)
            .Include(c => c.StatusHistory)
            .FirstOrDefaultAsync(c => c.Id == clientId);
    }

    private async Task<int> CountClientsAsync(Guid tenantId)
    {
        await using var db = _factory.CreateContext();
        return await db.Clients.IgnoreQueryFilters().CountAsync(c => c.TenantId == tenantId);
    }

    private string DocumentBlindIndex(string documentNumber) => _indexer.Compute(
        BlindIndexPurpose.IdentityDocument,
        SensitiveValueNormalizer.NormalizeDocumentNumber(documentNumber));

    // ── Nominal conversion ──────────────────────────────────────────────────

    [Fact]
    public async Task Creates_the_client_with_the_fields_the_lead_already_collected()
    {
        var handler = BuildHandler(out _, out _);

        var result = await handler.Handle(Command(), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.AlreadyExisted.Should().BeFalse();
        result.Value.BlockingCode.Should().BeNull();
        result.Value.ClientNumber.Should().Be(LeadConversionTestDoubles.ClientNumber);

        var client = await LoadAsync(result.Value.ClientId);

        client.Should().NotBeNull();
        client!.Type.Should().Be(ClientType.Individual);
        client.Status.Should().Be(ClientStatus.PendingKyc);
        client.FirstName.Should().Be("Awa");
        client.LastName.Should().Be("Kone");
        client.Gender.Should().Be(Gender.Female);
        client.Nationality.Should().Be("CI");
        client.Profession.Should().Be("Commerçante");
        client.PreferredLanguage.Should().Be("fr");
        client.AgencyId.Should().Be(AgencyId);
        client.AgencyCode.Should().Be(LeadConversionTestDoubles.AgencyCode);
        client.CreatedBy.Should().Be(ConvertingAgentId);
        client.PhoneticKeyPrimary.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task Records_the_lead_it_was_converted_from()
    {
        var handler = BuildHandler(out _, out _);

        var result = await handler.Handle(Command(), CancellationToken.None);

        var client = await LoadAsync(result.Value.ClientId);

        client!.SourceLeadId.Should().Be(LeadId);
    }

    [Fact]
    public async Task Stores_the_protected_fields_encrypted_and_blind_indexed_never_in_clear()
    {
        var handler = BuildHandler(out _, out _);

        var result = await handler.Handle(Command(), CancellationToken.None);

        var client = await LoadAsync(result.Value.ClientId);

        client!.EncryptedIdentityDocumentNumber.Should().NotBeNullOrEmpty();
        client.EncryptedIdentityDocumentNumber.Should().NotBe(DocumentNumber);
        client.IdentityDocumentNumberBlindIndex.Should().Be(DocumentBlindIndex(DocumentNumber));

        client.EncryptedDateOfBirth.Should().NotBeNullOrEmpty();
        client.EncryptedDateOfBirth.Should().NotBe(BirthDateIso);
        client.DateOfBirthBlindIndex.Should().NotBeNullOrWhiteSpace();

        client.ContactPoints.Should().OnlyContain(cp => cp.EncryptedValue != PhoneNumber);
        client.ContactPoints.Should().OnlyContain(cp => cp.EncryptedValue != Email);
    }

    [Fact]
    public async Task Creates_the_phone_and_email_contact_points_with_the_phone_as_primary()
    {
        var handler = BuildHandler(out _, out _);

        var result = await handler.Handle(Command(), CancellationToken.None);

        var client = await LoadAsync(result.Value.ClientId);

        client!.ContactPoints.Should().HaveCount(2);

        var phone = client.ContactPoints.Single(cp => cp.Type == ContactPointType.Phone);
        phone.IsPrimary.Should().BeTrue();
        phone.IsActive.Should().BeTrue();
        phone.BlindIndex.Should().Be(
            _indexer.Compute(BlindIndexPurpose.Phone, SensitiveValueNormalizer.NormalizePhone(PhoneNumber)));

        var email = client.ContactPoints.Single(cp => cp.Type == ContactPointType.Email);
        email.BlindIndex.Should().Be(
            _indexer.Compute(BlindIndexPurpose.Email, SensitiveValueNormalizer.NormalizeEmail(Email)));
    }

    [Fact]
    public async Task Reuses_the_client_identifier_the_leads_module_already_emitted()
    {
        // M13 stamps Lead.CustomerId before calling M01; if M01 minted its own id the two
        // records would stop pointing at each other.
        var requested = Guid.Parse("dddddddd-0000-0000-0000-000000000009");
        var handler = BuildHandler(out _, out _);

        var result = await handler.Handle(
            Command(requestedClientId: requested), CancellationToken.None);

        result.Value.ClientId.Should().Be(requested);

        var client = await LoadAsync(requested);
        client.Should().NotBeNull();
        client!.StatusHistory.Should().OnlyContain(h => h.ClientId == requested);
        client.ContactPoints.Should().OnlyContain(cp => cp.ClientId == requested);
    }

    // ── Idempotence ─────────────────────────────────────────────────────────

    [Fact]
    public async Task Converting_the_same_lead_twice_returns_the_same_client_and_creates_no_duplicate()
    {
        var first = await BuildHandler(out _, out _).Handle(Command(), CancellationToken.None);
        var second = await BuildHandler(out _, out _).Handle(Command(), CancellationToken.None);

        first.IsSuccess.Should().BeTrue();
        second.IsSuccess.Should().BeTrue();

        second.Value.AlreadyExisted.Should().BeTrue();
        second.Value.ClientId.Should().Be(first.Value.ClientId);
        second.Value.ClientNumber.Should().Be(first.Value.ClientNumber);
        second.Value.BlockingCode.Should().BeNull();

        (await CountClientsAsync(TenantId)).Should().Be(1);
    }

    [Fact]
    public async Task Does_not_publish_a_second_creation_event_when_the_lead_was_already_converted()
    {
        await BuildHandler(out _, out _).Handle(Command(), CancellationToken.None);

        var handler = BuildHandler(out var publisher, out _);
        await handler.Handle(Command(), CancellationToken.None);

        publisher.OfType<ClientCreatedEvent>().Should().BeEmpty();
    }

    // ── Advisor inheritance ─────────────────────────────────────────────────

    [Fact]
    public async Task Assigns_the_converting_agent_as_advisor_when_the_tenant_parameter_is_enabled()
    {
        var handler = BuildHandler(out _, out _, advisorFromConvertingAgent: true);

        var result = await handler.Handle(Command(), CancellationToken.None);

        var client = await LoadAsync(result.Value.ClientId);
        client!.AdvisorUserId.Should().Be(ConvertingAgentId);
    }

    [Fact]
    public async Task Leaves_the_client_without_an_advisor_when_the_tenant_parameter_is_disabled()
    {
        var handler = BuildHandler(out _, out _, advisorFromConvertingAgent: false);

        var result = await handler.Handle(Command(), CancellationToken.None);

        var client = await LoadAsync(result.Value.ClientId);
        client!.AdvisorUserId.Should().BeNull();
    }

    // ── Integration event ───────────────────────────────────────────────────

    [Fact]
    public async Task Publishes_a_client_created_event_carrying_the_source_lead_id()
    {
        // The SourceLeadId on this event is what opens the KYC file in module M02 and what the
        // timeline slice uses to import the lead's history — it is not decoration.
        var handler = BuildHandler(out var publisher, out _);

        var result = await handler.Handle(Command(), CancellationToken.None);

        var published = publisher.OfType<ClientCreatedEvent>().Should().ContainSingle().Subject;

        published.TenantId.Should().Be(TenantId);
        published.ClientId.Should().Be(result.Value.ClientId);
        published.ClientNumber.Should().Be(LeadConversionTestDoubles.ClientNumber);
        published.ClientType.Should().Be(nameof(ClientType.Individual));
        published.AgencyId.Should().Be(AgencyId);
        published.AdvisorUserId.Should().Be(ConvertingAgentId);
        published.SourceLeadId.Should().Be(LeadId);
        published.CreatedBy.Should().Be(ConvertingAgentId);
    }

    // ── Blocking duplicate ──────────────────────────────────────────────────

    [Fact]
    public async Task Returns_the_blocking_code_and_the_existing_client_when_the_document_is_already_used()
    {
        var existing = await SeedExistingHolderOfTheDocumentAsync();

        var handler = BuildHandler(out var publisher, out _);
        var result = await handler.Handle(Command(), CancellationToken.None);

        // A SUCCESS carrying a code, not a failure: M13 needs the existing client's id and
        // number to offer "attach this lead to that client instead".
        result.IsSuccess.Should().BeTrue();
        result.Value.BlockingCode.Should().Be(CustomerErrors.DuplicateIdentityDocument);
        result.Value.ClientId.Should().Be(existing.Id);
        result.Value.ClientNumber.Should().Be(existing.ClientNumber);
        result.Value.AlreadyExisted.Should().BeFalse();

        (await CountClientsAsync(TenantId)).Should().Be(1);
        publisher.OfType<ClientCreatedEvent>().Should().BeEmpty();
    }

    [Fact]
    public async Task Never_decrypts_a_protected_column_while_probing_for_a_duplicate_document()
    {
        await SeedExistingHolderOfTheDocumentAsync();

        var handler = BuildHandler(out _, out var encryptor);
        await handler.Handle(Command(), CancellationToken.None);

        encryptor.DecryptCalls.Should().Be(0);
    }

    private async Task<Client> SeedExistingHolderOfTheDocumentAsync()
    {
        var existing = LeadConversionTestDoubles.ExistingIndividual(
            TenantId, AgencyId, ConvertingAgentId,
            clientNumber: "AG000001-2026-000001",
            documentBlindIndex: DocumentBlindIndex(DocumentNumber));

        await using var db = _factory.CreateContext();
        db.Clients.Add(existing);
        await db.SaveChangesAsync();

        return existing;
    }

    // ── Legal entity ────────────────────────────────────────────────────────

    [Fact]
    public async Task Creates_a_legal_client_when_the_lead_only_carries_a_legal_name()
    {
        await using (var seed = _factory.CreateContext())
            await LeadConversionTestDoubles.SeedLegalFormsAsync(seed, TenantId, "SARL", "SA");

        var handler = BuildHandler(out var publisher, out _);

        var result = await handler.Handle(
            Command(firstName: null, lastName: null, legalName: "Sankore Distribution", documentNumber: null),
            CancellationToken.None);

        result.IsSuccess.Should().BeTrue();

        var client = await LoadAsync(result.Value.ClientId);
        client!.Type.Should().Be(ClientType.Legal);
        client.LegalName.Should().Be("Sankore Distribution");
        // No legal form travels on a lead: the tenant's first active entry is taken and the
        // operator refines it during KYC.
        client.LegalFormCode.Should().Be("SARL");
        client.SourceLeadId.Should().Be(LeadId);

        publisher.OfType<ClientCreatedEvent>().Should().ContainSingle()
            .Which.ClientType.Should().Be(nameof(ClientType.Legal));
    }

    [Fact]
    public async Task Refuses_a_legal_lead_when_the_tenant_has_no_active_legal_form()
    {
        var handler = BuildHandler(out _, out _);

        var result = await handler.Handle(
            Command(firstName: null, lastName: null, legalName: "Sankore Distribution", documentNumber: null),
            CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Be(CustomerErrors.LegalFormUnknown);
    }

    [Fact]
    public async Task Refuses_a_lead_with_neither_a_person_name_nor_a_legal_name()
    {
        var handler = BuildHandler(out _, out _);

        var result = await handler.Handle(
            Command(firstName: null, lastName: null, legalName: null, documentNumber: null),
            CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Be("LEAD_IDENTITY_INCOMPLETE");
    }

    // ── Agency ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task Refuses_the_conversion_when_the_agency_cannot_be_resolved()
    {
        var handler = BuildHandler(out _, out _, agencyCode: null);

        var result = await handler.Handle(Command(), CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Be(CustomerErrors.AgencyOutOfScope);
        (await CountClientsAsync(TenantId)).Should().Be(0);
    }

    // ── Multi-tenant isolation ──────────────────────────────────────────────

    [Fact]
    public async Task A_lead_of_another_tenant_never_reaches_this_tenants_clients()
    {
        // Tenant A already converted lead L. The ambient tenant context is A, but the request
        // says B — only the explicit TenantId predicate keeps the two apart, so tenant B must
        // NOT be told "already converted", and its new client must belong to B alone.
        var tenantAClient = LeadConversionTestDoubles.ExistingIndividual(
            TenantId, AgencyId, ConvertingAgentId,
            clientNumber: "AG000001-2026-000001",
            sourceLeadId: LeadId);

        await using (var seed = _factory.CreateContext())
        {
            seed.Clients.Add(tenantAClient);
            await seed.SaveChangesAsync();
        }

        var handler = BuildHandler(out var publisher, out _);
        var result = await handler.Handle(
            Command(tenantId: OtherTenantId, documentNumber: null), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.AlreadyExisted.Should().BeFalse();
        result.Value.ClientId.Should().NotBe(tenantAClient.Id);

        var created = await LoadAsync(result.Value.ClientId);
        created!.TenantId.Should().Be(OtherTenantId);

        publisher.OfType<ClientCreatedEvent>().Should().ContainSingle()
            .Which.TenantId.Should().Be(OtherTenantId);

        (await CountClientsAsync(TenantId)).Should().Be(1);
        (await CountClientsAsync(OtherTenantId)).Should().Be(1);

        // And the tenant-A context cannot see tenant B's client through the global filter.
        await using var filtered = _factory.CreateContext();
        (await filtered.Clients.AnyAsync(c => c.Id == result.Value.ClientId)).Should().BeFalse();
    }

    [Fact]
    public async Task A_document_registered_in_another_tenant_does_not_block_this_tenant()
    {
        var otherTenantHolder = LeadConversionTestDoubles.ExistingIndividual(
            OtherTenantId, AgencyId, ConvertingAgentId,
            clientNumber: "AG000001-2026-000001",
            documentBlindIndex: DocumentBlindIndex(DocumentNumber));

        await using (var seed = _factory.CreateContext())
        {
            seed.Clients.Add(otherTenantHolder);
            await seed.SaveChangesAsync();
        }

        var handler = BuildHandler(out _, out _);
        var result = await handler.Handle(Command(), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.BlockingCode.Should().BeNull();
        result.Value.ClientId.Should().NotBe(otherTenantHolder.Id);
    }
}
