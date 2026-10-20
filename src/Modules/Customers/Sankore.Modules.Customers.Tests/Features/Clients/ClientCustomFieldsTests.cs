namespace Sankore.Modules.Customers.Tests.Features.Clients;

using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using NSubstitute;
using Sankore.Modules.Administration.PublicApi;
using Sankore.Modules.Administration.PublicApi.Forms;
using Sankore.Modules.Customers.Domain;
using Sankore.Modules.Customers.Features.Clients.CreateIndividualClient;
using Sankore.Modules.Customers.Features.Clients.Shared;
using Sankore.Modules.Customers.Features.LegalEntities.CreateLegalClient;
using Sankore.Modules.Customers.Infrastructure;
using Sankore.Modules.Customers.Tests.TestSupport;
using Sankore.Shared.Infrastructure.Outbox;
using Xunit;

/// <summary>
/// The client form applied to a client creation (dynamic forms, lot 1), and the native catalogue
/// pinned against this module's own types.
/// </summary>
public sealed class ClientCustomFieldsTests : IDisposable
{
    private static readonly Guid TenantId = Guid.NewGuid();
    private static readonly Guid AgencyId = Guid.NewGuid();
    private static readonly Guid ActorId = Guid.NewGuid();

    private readonly TestCustomersDbContextFactory _factory = new(TenantId);
    private readonly IAdministrationModule _admin = Substitute.For<IAdministrationModule>();

    public void Dispose() => _factory.Dispose();

    private static readonly FormField Capital = new(
        "capital", FormFieldType.Money, "Capital social", 1, "company",
        QuickAdd: true, IsRequired: true, VisibleWhen: new FormCondition("clientType", ["Legal"]));

    private static readonly FormField Channel = new(
        "acquisitionChannel", FormFieldType.SingleChoice, "Canal d'acquisition", 1, "contact",
        Options: [new FormOption("Agence", "Agence"), new FormOption("Parrainage", "Parrainage")]);

    private void TenantForm(params FormField[] extra)
    {
        var catalog = NativeFormCatalog.For(FormEntity.Client);
        _admin.GetEntityFormAsync(TenantId, FormEntity.Client, Arg.Any<CancellationToken>())
            .Returns(new EntityFormDto(FormEntity.Client, 1, catalog.Sections, [.. catalog.Fields, .. extra]));
    }

    private static Dictionary<string, JsonElement> Values(object values)
        => JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(JsonSerializer.Serialize(values))!;

    [Fact]
    public async Task An_individual_is_created_with_its_custom_values_and_a_legal_only_requirement_does_not_apply()
    {
        TenantForm(Capital, Channel);
        await using var db = _factory.CreateContext();

        var result = await Individual(db).Handle(
            IndividualCommand(Values(new { acquisitionChannel = "Parrainage" })), default);

        result.IsSuccess.Should().BeTrue(result.Error);
        var client = await db.Clients.SingleAsync();
        CustomFieldValues.Read(client.CustomFieldsJson)!["acquisitionChannel"].GetString().Should().Be("Parrainage");
    }

    [Fact]
    public async Task A_legal_entity_must_carry_its_required_custom_field()
    {
        TenantForm(Capital);
        await using var db = _factory.CreateContext();

        var refused = await Legal(db).Handle(LegalCommand(custom: null), default);
        refused.Error.Should().Be("CUSTOM_FIELD_REQUIRED:capital");
        (await db.Clients.CountAsync()).Should().Be(0);

        var created = await Legal(db).Handle(
            LegalCommand(Values(new { capital = new { amount = 10_000_000, currency = "XOF" } })), default);
        created.IsSuccess.Should().BeTrue(created.Error);
    }

    [Fact]
    public async Task The_file_import_is_not_refused_for_a_requirement_of_the_form()
    {
        TenantForm(Capital);
        await using var db = _factory.CreateContext();

        var result = await Legal(db).Handle(LegalCommand(custom: null) with { EnforceFormRequirements = false }, default);

        result.IsSuccess.Should().BeTrue(result.Error);
    }

    // ── Reads: a sensitive custom value never leaves in clear ─────────────

    private static readonly FormField Spouse = new(
        "spouseEmployer", FormFieldType.Text, "Employeur du conjoint", 2, "contact", IsSensitive: true);

    private async Task<Guid> ClientWithAsync(object values)
    {
        await using var db = _factory.CreateContext();
        var result = await Individual(db).Handle(IndividualCommand(Values(values)), default);
        result.IsSuccess.Should().BeTrue(result.Error);
        return result.Value.ClientId!.Value;
    }

    private async Task<ClientDetailDto> DetailAsync(Guid id)
    {
        await using var db = _factory.CreateContext();
        var result = await new Sankore.Modules.Customers.Features.Clients.GetClient.GetClientHandler(
                db, TestDoubles.CurrentUser(TenantId, ActorId), TestDoubles.AgencyScope(), TestDoubles.Encryptor(), _admin)
            .Handle(new Sankore.Modules.Customers.Features.Clients.GetClient.GetClientQuery(id), default);
        return result.Value;
    }

    [Fact]
    public async Task A_sensitive_custom_value_is_withheld_by_the_server_and_named_as_masked()
    {
        TenantForm(Channel, Spouse);
        var id = await ClientWithAsync(new { acquisitionChannel = "Agence", spouseEmployer = "Orange CI" });

        var detail = await DetailAsync(id);

        detail.CustomFields!.Keys.Should().Equal("acquisitionChannel");
        detail.MaskedCustomFieldKeys.Should().Equal("spouseEmployer");
        JsonSerializer.Serialize(detail).Should().NotContain("Orange CI");
    }

    [Fact]
    public async Task An_unreadable_form_withholds_every_custom_value()
    {
        TenantForm(Channel);
        var id = await ClientWithAsync(new { acquisitionChannel = "Agence" });

        _admin.GetEntityFormAsync(TenantId, FormEntity.Client, Arg.Any<CancellationToken>())
            .Returns<EntityFormDto>(_ => throw new InvalidOperationException("M12 down"));

        var detail = await DetailAsync(id);

        detail.CustomFields.Should().BeNullOrEmpty();
        detail.MaskedCustomFieldKeys.Should().Equal("acquisitionChannel");
    }

    [Fact]
    public async Task A_sensitive_custom_value_is_revealed_through_the_audited_endpoint()
    {
        TenantForm(Spouse);
        var id = await ClientWithAsync(new { spouseEmployer = "Orange CI" });

        await using var db = _factory.CreateContext();
        var reveal = await new Sankore.Modules.Customers.Features.Clients.RevealSensitiveField.RevealSensitiveFieldHandler(
                db, TestDoubles.CurrentUser(TenantId, ActorId), TestDoubles.AgencyScope(), TestDoubles.Settings(TenantId),
                TestDoubles.Encryptor(), Substitute.For<Sankore.Modules.Notifications.PublicApi.INotificationsModule>(),
                new OutboxEventPublisher<CustomersDbContext>(db),
                Microsoft.Extensions.Logging.Abstractions.NullLogger<Sankore.Modules.Customers.Features.Clients.RevealSensitiveField.RevealSensitiveFieldHandler>.Instance)
            .Handle(new Sankore.Modules.Customers.Features.Clients.RevealSensitiveField.RevealSensitiveFieldCommand(
                id, SensitiveField.CustomField, null, "spouseEmployer"), default);

        reveal.IsSuccess.Should().BeTrue(reveal.Error);
        reveal.Value.Value.Should().Be("Orange CI");
        (await db.SensitiveDataAccessLogs.SingleAsync()).FieldName.Should().Be("custom:spouseEmployer");
    }

    // ── The catalogue against this module's types ────────────────────────

    [Fact]
    public void Every_native_client_key_is_reported_by_one_of_the_two_creations()
    {
        var reported = CreateIndividualClientHandler.NativeFormValues(IndividualCommand(null)).Keys
            .Concat(CreateLegalClientHandler.NativeFormValues(LegalCommand(null)).Keys)
            .ToHashSet();

        NativeFormCatalog.For(FormEntity.Client).Fields.Select(f => f.Key).Should().BeSubsetOf(reported);
    }

    [Theory]
    [InlineData("clientType", typeof(ClientType))]
    [InlineData("gender", typeof(Gender))]
    [InlineData("maritalStatus", typeof(MaritalStatus))]
    [InlineData("identityDocumentType", typeof(IdentityDocumentType))]
    public void Native_client_options_are_the_enum_names(string key, Type enumType)
    {
        NativeFormCatalog.For(FormEntity.Client).Find(key)!.Options!.Select(o => o.Value)
            .Should().BeEquivalentTo(Enum.GetNames(enumType));
    }

    // ─────────────────────────────────────────────────────────────────────

    private CreateIndividualClientHandler Individual(CustomersDbContext db) => new(
        db,
        TestDoubles.CurrentUser(TenantId, ActorId),
        TestDoubles.Settings(TenantId),
        new DuplicateProbe(db),
        ClientsTestHarness.AgencyDirectory(TenantId, AgencyId),
        ClientsTestHarness.ClientNumbers(),
        TestDoubles.Encryptor(),
        TestDoubles.Indexer(),
        ClientsTestHarness.PhoneticKeys(),
        new OutboxEventPublisher<CustomersDbContext>(db),
        _admin);

    private CreateLegalClientHandler Legal(CustomersDbContext db)
    {
        if (!db.LegalForms.Any(f => f.Code == "SARL"))
        {
            db.LegalForms.Add(LegalForm.Create(TenantId, "SARL", "Société à responsabilité limitée", 1));
            db.SaveChanges();
        }

        return new CreateLegalClientHandler(
            db,
            new Sankore.Shared.Kernel.FixedTenantContext(TenantId),
            TestDoubles.CurrentUser(TenantId, ActorId),
            new DuplicateProbe(db),
            ClientsTestHarness.AgencyDirectory(TenantId, AgencyId),
            ClientsTestHarness.ClientNumbers(),
            TestDoubles.Encryptor(),
            TestDoubles.Indexer(),
            ClientsTestHarness.PhoneticKeys(),
            new OutboxEventPublisher<CustomersDbContext>(db),
            _admin);
    }

    private static CreateIndividualClientCommand IndividualCommand(IReadOnlyDictionary<string, JsonElement>? custom) => new(
        AgencyId: AgencyId, AdvisorUserId: null, FirstName: "Awa", LastName: "Traoré", MaidenName: null,
        Gender: Gender.Female, DateOfBirth: new DateOnly(1990, 4, 12), BirthPlace: null, Nationality: "CI",
        MaritalStatus: null, FatherName: null, MotherName: null, Profession: null, Employer: null,
        DeclaredIncome: null, DeclaredIncomeCurrency: null, PreferredLanguage: "fr",
        IdentityDocumentType: IdentityDocumentType.NationalIdCard, IdentityDocumentNumber: "CI0099887766",
        IdentityDocumentIssuedOn: null, IdentityDocumentExpiresOn: null,
        PhoneNumbers: ["+225 07 11 22 33"], Email: null, Address: null, ConfirmNoDuplicate: true,
        CustomFields: custom, EnforceFormRequirements: true);

    private static CreateLegalClientCommand LegalCommand(IReadOnlyDictionary<string, JsonElement>? custom) => new(
        AgencyId: AgencyId, AdvisorUserId: null, LegalName: "Ets Koné & Fils", LegalFormCode: "SARL",
        RegistrationNumber: "CI-ABJ-2020-B-1234", TaxIdNumber: null, IncorporationDate: new DateOnly(2020, 1, 15),
        HeadOfficeAddress: new PostalAddressInput("Rue 12", "Abidjan", null, "CI", null),
        PhoneNumbers: ["+225 27 20 00 00"], Email: null, PreferredLanguage: "fr",
        CustomFields: custom, EnforceFormRequirements: true);
}
