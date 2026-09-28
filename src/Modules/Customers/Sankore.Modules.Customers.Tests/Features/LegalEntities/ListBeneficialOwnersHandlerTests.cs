namespace Sankore.Modules.Customers.Tests.Features.LegalEntities;

using FluentAssertions;
using Sankore.Modules.Customers.Domain;
using Sankore.Modules.Customers.Features.LegalEntities.ListBeneficialOwners;
using Sankore.Modules.Customers.Tests.TestSupport;
using Sankore.Shared.Infrastructure.Crypto;
using Xunit;

/// <summary>US-M01-BE-18 read side — masking and perimeter.</summary>
public sealed class ListBeneficialOwnersHandlerTests : IDisposable
{
    private static readonly Guid TenantId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid OtherTenantId = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid AgencyId = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001");
    private static readonly Guid OtherAgencyId = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000002");
    private static readonly Guid UserId = Guid.Parse("bbbbbbbb-0000-0000-0000-000000000001");

    private const string DocumentNumber = "CI0123456789";

    private readonly TestCustomersDbContextFactory _factory = new(TenantId);
    private readonly IFieldEncryptor _encryptor = TestDoubles.Encryptor();

    public void Dispose() => _factory.Dispose();

    private ListBeneficialOwnersHandler BuildHandler(params Guid[] accessibleAgencies)
        => new(
            _factory.CreateContext(),
            TestDoubles.CurrentUser(TenantId, UserId),
            TestDoubles.AgencyScope(accessibleAgencies),
            _encryptor);

    private async Task<Client> SeedAsync(Guid tenantId, Guid agencyId, bool withClosedOwner = true)
    {
        var client = LegalEntitiesTestDoubles.LegalClient(tenantId, agencyId, UserId);

        var active = BeneficialOwner.ForExternalPerson(
            tenantId: tenantId,
            legalClientId: client.Id,
            externalFullName: "Kouassi Adjoua",
            externalNationality: "CI",
            externalDateOfBirth: new DateOnly(1982, 7, 3),
            encryptedExternalDocumentNumber: _encryptor.Encrypt(DocumentNumber),
            externalDocumentBlindIndex: "index",
            ownershipPercentage: 100m,
            controlType: ControlType.Ownership,
            validFrom: DateTimeOffset.UtcNow.AddYears(-1),
            createdBy: UserId);

        await using var seed = _factory.CreateContext();
        seed.Clients.Add(client);
        seed.BeneficialOwners.Add(active);

        if (withClosedOwner)
        {
            var former = BeneficialOwner.ForExternalPerson(
                tenantId: tenantId,
                legalClientId: client.Id,
                externalFullName: "Traore Ibrahim",
                externalNationality: "CI",
                externalDateOfBirth: null,
                encryptedExternalDocumentNumber: null,
                externalDocumentBlindIndex: null,
                ownershipPercentage: 40m,
                controlType: ControlType.VotingRights,
                validFrom: DateTimeOffset.UtcNow.AddYears(-3),
                createdBy: UserId);

            former.Close(DateTimeOffset.UtcNow.AddYears(-1));
            seed.BeneficialOwners.Add(former);
        }

        await seed.SaveChangesAsync();
        return client;
    }

    [Fact]
    public async Task Returns_the_active_owners_with_the_identity_document_masked()
    {
        var client = await SeedAsync(TenantId, AgencyId);
        var handler = BuildHandler();

        var result = await handler.Handle(new ListBeneficialOwnersQuery(client.Id), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        var owner = result.Value.Should().ContainSingle().Subject;

        owner.ExternalFullName.Should().Be("Kouassi Adjoua");
        owner.IsActive.Should().BeTrue();
        owner.ControlType.Should().Be(nameof(ControlType.Ownership));

        // The clear document number must never appear in a read response.
        owner.ExternalDocumentNumberMasked.Should()
            .NotBeNullOrEmpty().And
            .NotBe(DocumentNumber).And
            .Be(SensitiveValueMasker.MaskDocument(DocumentNumber));

        owner.ExternalDateOfBirthMasked.Should()
            .Be(SensitiveValueMasker.MaskDate(new DateOnly(1982, 7, 3)));
    }

    [Fact]
    public async Task Hides_closed_owners_by_default_and_returns_them_on_demand()
    {
        var client = await SeedAsync(TenantId, AgencyId);

        var activeOnly = await BuildHandler().Handle(
            new ListBeneficialOwnersQuery(client.Id), CancellationToken.None);
        activeOnly.Value.Should().HaveCount(1);

        var withHistory = await BuildHandler().Handle(
            new ListBeneficialOwnersQuery(client.Id, IncludeClosed: true), CancellationToken.None);

        withHistory.Value.Should().HaveCount(2);
        withHistory.Value.Should().ContainSingle(o => !o.IsActive)
            .Which.ValidTo.Should().NotBeNull();
        // Active rows first, then history.
        withHistory.Value[0].IsActive.Should().BeTrue();
    }

    [Fact]
    public async Task Resolves_the_client_number_of_a_linked_owner()
    {
        var client = await SeedAsync(TenantId, AgencyId, withClosedOwner: false);
        var shareholder = LegalEntitiesTestDoubles.IndividualClient(TenantId, AgencyId, UserId);

        await using (var seed = _factory.CreateContext())
        {
            seed.Clients.Add(shareholder);
            seed.BeneficialOwners.Add(BeneficialOwner.ForClient(
                tenantId: TenantId,
                legalClientId: client.Id,
                linkedClientId: shareholder.Id,
                ownershipPercentage: 0m,
                controlType: ControlType.Manager,
                validFrom: DateTimeOffset.UtcNow,
                createdBy: UserId));
            await seed.SaveChangesAsync();
        }

        var result = await BuildHandler().Handle(
            new ListBeneficialOwnersQuery(client.Id), CancellationToken.None);

        result.Value.Should().ContainSingle(o => o.LinkedClientId == shareholder.Id)
            .Which.Should().Match<BeneficialOwnerDto>(o =>
                o.LinkedClientNumber == shareholder.ClientNumber
                && o.LinkedClientDisplayName == shareholder.DisplayName);
    }

    [Fact]
    public async Task Answers_not_found_for_an_individual_client_is_reported_as_not_a_legal_entity()
    {
        var individual = LegalEntitiesTestDoubles.IndividualClient(TenantId, AgencyId, UserId);
        await using (var seed = _factory.CreateContext())
        {
            seed.Clients.Add(individual);
            await seed.SaveChangesAsync();
        }

        var result = await BuildHandler().Handle(
            new ListBeneficialOwnersQuery(individual.Id), CancellationToken.None);

        result.Error.Should().Be("CLIENT_NOT_LEGAL_ENTITY");
    }

    [Fact]
    public async Task Answers_not_found_outside_the_agency_perimeter()
    {
        var client = await SeedAsync(TenantId, AgencyId);

        var result = await BuildHandler(OtherAgencyId).Handle(
            new ListBeneficialOwnersQuery(client.Id), CancellationToken.None);

        result.Error.Should().Be(CustomerErrors.ClientNotFound);
    }

    [Fact]
    public async Task Cannot_read_the_owners_of_a_client_of_another_tenant()
    {
        var foreign = await SeedAsync(OtherTenantId, AgencyId);

        var result = await BuildHandler().Handle(
            new ListBeneficialOwnersQuery(foreign.Id), CancellationToken.None);

        result.Error.Should().Be(CustomerErrors.ClientNotFound);
    }
}
