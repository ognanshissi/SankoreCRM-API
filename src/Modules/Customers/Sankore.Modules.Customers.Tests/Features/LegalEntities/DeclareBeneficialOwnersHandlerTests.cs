namespace Sankore.Modules.Customers.Tests.Features.LegalEntities;

using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Customers.Domain;
using Sankore.Modules.Customers.Features.LegalEntities.DeclareBeneficialOwners;
using Sankore.Modules.Customers.PublicApi.Events;
using Sankore.Modules.Customers.Tests.TestSupport;
using Sankore.Shared.Infrastructure.Crypto;
using Xunit;

/// <summary>US-M01-BE-18 — set-based declaration of the beneficial-owner structure.</summary>
public sealed class DeclareBeneficialOwnersHandlerTests : IDisposable
{
    private static readonly Guid TenantId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid OtherTenantId = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid AgencyId = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001");
    private static readonly Guid OtherAgencyId = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000002");
    private static readonly Guid UserId = Guid.Parse("bbbbbbbb-0000-0000-0000-000000000001");

    private const string Reason = "Annual AML review of the ownership structure";

    private readonly TestCustomersDbContextFactory _factory = new(TenantId);
    private readonly IFieldEncryptor _encryptor = TestDoubles.Encryptor();
    private readonly IBlindIndexer _indexer = TestDoubles.Indexer();

    public void Dispose() => _factory.Dispose();

    private DeclareBeneficialOwnersHandler BuildHandler(
        out RecordingEventPublisher publisher,
        string threshold = "25",
        params Guid[] accessibleAgencies)
    {
        publisher = new RecordingEventPublisher();

        return new DeclareBeneficialOwnersHandler(
            _factory.CreateContext(),
            TestDoubles.CurrentUser(TenantId, UserId),
            TestDoubles.AgencyScope(accessibleAgencies),
            TestDoubles.Settings(TenantId, (CustomerSettingKeys.BeneficialOwnerThreshold, threshold)),
            _encryptor,
            _indexer,
            publisher);
    }

    private async Task<Client> SeedLegalClientAsync(Guid tenantId, Guid agencyId)
    {
        var client = LegalEntitiesTestDoubles.LegalClient(tenantId, agencyId, UserId);
        await using var seed = _factory.CreateContext();
        seed.Clients.Add(client);
        await seed.SaveChangesAsync();
        return client;
    }

    private static BeneficialOwnerInput External(
        string name,
        decimal ownership,
        ControlType controlType = ControlType.Ownership,
        string? documentNumber = null) =>
        new(
            LinkedClientId: null,
            ExternalFullName: name,
            ExternalNationality: "CI",
            ExternalDateOfBirth: new DateOnly(1982, 7, 3),
            ExternalDocumentNumber: documentNumber,
            OwnershipPercentage: ownership,
            ControlType: controlType);

    [Fact]
    public async Task Records_the_declared_owners_and_publishes_the_structure_change()
    {
        var client = await SeedLegalClientAsync(TenantId, AgencyId);
        var handler = BuildHandler(out var publisher);

        var result = await handler.Handle(new DeclareBeneficialOwnersCommand(
            client.Id,
            [External("Kouassi Adjoua", 60m), External("Traore Ibrahim", 40m)],
            Reason), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().BeEquivalentTo(new DeclareBeneficialOwnersResult(2, 2, 0));

        publisher.OfType<BeneficialOwnersChangedEvent>().Should().ContainSingle()
            .Which.Should().Match<BeneficialOwnersChangedEvent>(e =>
                e.TenantId == TenantId
                && e.LegalClientId == client.Id
                && e.ActiveOwnerCount == 2
                && e.ActorUserId == UserId);

        await using var assertions = _factory.CreateContext();
        var stored = await assertions.BeneficialOwners
            .Where(o => o.LegalClientId == client.Id)
            .ToListAsync();

        stored.Should().HaveCount(2);
        stored.Should().OnlyContain(o => o.ValidTo == null);
        stored.Sum(o => o.OwnershipPercentage).Should().Be(100m);
    }

    [Fact]
    public async Task Refuses_a_declaration_whose_stakes_exceed_one_hundred_percent()
    {
        var client = await SeedLegalClientAsync(TenantId, AgencyId);
        var handler = BuildHandler(out var publisher);

        var result = await handler.Handle(new DeclareBeneficialOwnersCommand(
            client.Id,
            [External("Kouassi Adjoua", 60m), External("Traore Ibrahim", 50m)],
            Reason), CancellationToken.None);

        result.Error.Should().Be(CustomerErrors.OwnershipExceeds100);
        publisher.Published.Should().BeEmpty();

        await using var assertions = _factory.CreateContext();
        (await assertions.BeneficialOwners.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task Requires_a_manager_when_nobody_reaches_the_tenant_threshold()
    {
        var client = await SeedLegalClientAsync(TenantId, AgencyId);
        var handler = BuildHandler(out _);

        // Five shareholders at 20 %: control is exercised otherwise, so a manager must be named.
        var owners = Enumerable.Range(1, 5)
            .Select(i => External($"Shareholder {i}", 20m))
            .ToList();

        var result = await handler.Handle(
            new DeclareBeneficialOwnersCommand(client.Id, owners, Reason), CancellationToken.None);

        result.Error.Should().Be(CustomerErrors.ManagerBeneficialOwnerRequired);
    }

    [Fact]
    public async Task Accepts_a_dispersed_structure_once_a_manager_is_named()
    {
        var client = await SeedLegalClientAsync(TenantId, AgencyId);
        var handler = BuildHandler(out _);

        var owners = Enumerable.Range(1, 5)
            .Select(i => External($"Shareholder {i}", 20m))
            .Append(External("Yao Kone", 0m, ControlType.Manager))
            .ToList();

        var result = await handler.Handle(
            new DeclareBeneficialOwnersCommand(client.Id, owners, Reason), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.ActiveOwnerCount.Should().Be(6);
    }

    [Fact]
    public async Task Accepts_a_structure_without_a_manager_when_someone_reaches_the_threshold()
    {
        var client = await SeedLegalClientAsync(TenantId, AgencyId);
        var handler = BuildHandler(out _);

        var result = await handler.Handle(new DeclareBeneficialOwnersCommand(
            client.Id, [External("Kouassi Adjoua", 25m)], Reason), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
    }

    [Fact]
    public async Task Honours_a_tenant_threshold_raised_above_the_default()
    {
        var client = await SeedLegalClientAsync(TenantId, AgencyId);
        // 30 % threshold: a 25 % holder no longer clears it, so a manager becomes mandatory.
        var handler = BuildHandler(out _, threshold: "30");

        var result = await handler.Handle(new DeclareBeneficialOwnersCommand(
            client.Id, [External("Kouassi Adjoua", 25m)], Reason), CancellationToken.None);

        result.Error.Should().Be(CustomerErrors.ManagerBeneficialOwnerRequired);
    }

    [Fact]
    public async Task Closes_the_owner_absent_from_the_new_list_instead_of_deleting_it()
    {
        var client = await SeedLegalClientAsync(TenantId, AgencyId);
        var handler = BuildHandler(out _);

        await handler.Handle(new DeclareBeneficialOwnersCommand(
            client.Id, [External("Kouassi Adjoua", 100m)], Reason), CancellationToken.None);

        var replacement = BuildHandler(out var publisher);
        var result = await replacement.Handle(new DeclareBeneficialOwnersCommand(
            client.Id, [External("Traore Ibrahim", 100m)], Reason), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().BeEquivalentTo(new DeclareBeneficialOwnersResult(1, 1, 1));

        publisher.OfType<BeneficialOwnersChangedEvent>().Should().ContainSingle()
            .Which.ActiveOwnerCount.Should().Be(1);

        await using var assertions = _factory.CreateContext();
        var rows = await assertions.BeneficialOwners
            .Where(o => o.LegalClientId == client.Id)
            .ToListAsync();

        // Two rows: the AML trail keeps the former owner, closed by ValidTo.
        rows.Should().HaveCount(2);
        rows.Should().ContainSingle(o => o.ExternalFullName == "Kouassi Adjoua")
            .Which.ValidTo.Should().NotBeNull();
        rows.Should().ContainSingle(o => o.ExternalFullName == "Traore Ibrahim")
            .Which.ValidTo.Should().BeNull();
    }

    [Fact]
    public async Task Re_declaring_the_identical_structure_changes_nothing()
    {
        var client = await SeedLegalClientAsync(TenantId, AgencyId);
        var owners = new[] { External("Kouassi Adjoua", 55m), External("Traore Ibrahim", 45m) };

        await BuildHandler(out _).Handle(
            new DeclareBeneficialOwnersCommand(client.Id, owners, Reason), CancellationToken.None);

        var result = await BuildHandler(out _).Handle(
            new DeclareBeneficialOwnersCommand(client.Id, owners, Reason), CancellationToken.None);

        result.Value.Should().BeEquivalentTo(new DeclareBeneficialOwnersResult(2, 0, 0));

        await using var assertions = _factory.CreateContext();
        (await assertions.BeneficialOwners.CountAsync(o => o.LegalClientId == client.Id)).Should().Be(2);
    }

    [Fact]
    public async Task Historizes_a_change_of_stake_for_the_same_person()
    {
        var client = await SeedLegalClientAsync(TenantId, AgencyId);

        await BuildHandler(out _).Handle(new DeclareBeneficialOwnersCommand(
            client.Id, [External("Kouassi Adjoua", 100m)], Reason), CancellationToken.None);

        var result = await BuildHandler(out _).Handle(new DeclareBeneficialOwnersCommand(
            client.Id, [External("Kouassi Adjoua", 60m), External("Traore Ibrahim", 40m)],
            Reason), CancellationToken.None);

        result.Value.Should().BeEquivalentTo(new DeclareBeneficialOwnersResult(2, 2, 1));

        await using var assertions = _factory.CreateContext();
        var rows = await assertions.BeneficialOwners
            .Where(o => o.LegalClientId == client.Id)
            .ToListAsync();

        rows.Should().HaveCount(3);
        rows.Should().ContainSingle(o => o.OwnershipPercentage == 100m && o.ValidTo != null);
        rows.Where(o => o.ValidTo == null).Sum(o => o.OwnershipPercentage).Should().Be(100m);
    }

    [Fact]
    public async Task Stores_the_identity_document_of_an_external_owner_encrypted()
    {
        var client = await SeedLegalClientAsync(TenantId, AgencyId);
        var handler = BuildHandler(out _);

        await handler.Handle(new DeclareBeneficialOwnersCommand(
            client.Id,
            [External("Kouassi Adjoua", 100m, documentNumber: "CI0123456789")],
            Reason), CancellationToken.None);

        await using var assertions = _factory.CreateContext();
        var stored = await assertions.BeneficialOwners.SingleAsync(o => o.LegalClientId == client.Id);

        stored.EncryptedExternalDocumentNumber.Should().NotBeNullOrEmpty().And.NotBe("CI0123456789");
        _encryptor.Decrypt(stored.EncryptedExternalDocumentNumber).Should().Be("CI0123456789");
        stored.ExternalDocumentBlindIndex.Should()
            .Be(_indexer.Compute(BlindIndexPurpose.IdentityDocument, "CI0123456789"));
    }

    [Fact]
    public async Task Links_an_existing_client_as_a_beneficial_owner()
    {
        var client = await SeedLegalClientAsync(TenantId, AgencyId);
        var shareholder = LegalEntitiesTestDoubles.IndividualClient(TenantId, AgencyId, UserId);

        await using (var seed = _factory.CreateContext())
        {
            seed.Clients.Add(shareholder);
            await seed.SaveChangesAsync();
        }

        var handler = BuildHandler(out _);

        var result = await handler.Handle(new DeclareBeneficialOwnersCommand(
            client.Id,
            [new BeneficialOwnerInput(shareholder.Id, null, null, null, null, 100m, ControlType.Ownership)],
            Reason), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();

        await using var assertions = _factory.CreateContext();
        var stored = await assertions.BeneficialOwners.SingleAsync(o => o.LegalClientId == client.Id);
        stored.LinkedClientId.Should().Be(shareholder.Id);
        stored.ExternalFullName.Should().BeNull();
    }

    [Fact]
    public async Task Refuses_a_declaration_aimed_at_an_individual_client()
    {
        var individual = LegalEntitiesTestDoubles.IndividualClient(TenantId, AgencyId, UserId);
        await using (var seed = _factory.CreateContext())
        {
            seed.Clients.Add(individual);
            await seed.SaveChangesAsync();
        }

        var handler = BuildHandler(out _);

        var result = await handler.Handle(new DeclareBeneficialOwnersCommand(
            individual.Id, [External("Kouassi Adjoua", 100m)], Reason), CancellationToken.None);

        result.Error.Should().Be("CLIENT_NOT_LEGAL_ENTITY");
    }

    [Fact]
    public async Task Refuses_a_declaration_on_an_archived_client()
    {
        var client = LegalEntitiesTestDoubles.LegalClient(TenantId, AgencyId, UserId);
        client.Archive("End of activity", UserId).IsSuccess.Should().BeTrue();

        await using (var seed = _factory.CreateContext())
        {
            seed.Clients.Add(client);
            await seed.SaveChangesAsync();
        }

        var handler = BuildHandler(out _);

        var result = await handler.Handle(new DeclareBeneficialOwnersCommand(
            client.Id, [External("Kouassi Adjoua", 100m)], Reason), CancellationToken.None);

        result.Error.Should().Be(CustomerErrors.ClientReadOnly);
    }

    [Fact]
    public async Task Requires_a_reason()
    {
        var client = await SeedLegalClientAsync(TenantId, AgencyId);
        var handler = BuildHandler(out _);

        var result = await handler.Handle(new DeclareBeneficialOwnersCommand(
            client.Id, [External("Kouassi Adjoua", 100m)], "   "), CancellationToken.None);

        result.Error.Should().Be(CustomerErrors.ReasonRequired);
    }

    [Fact]
    public async Task Answers_not_found_for_an_unknown_client()
    {
        var handler = BuildHandler(out _);

        var result = await handler.Handle(new DeclareBeneficialOwnersCommand(
            Guid.NewGuid(), [External("Kouassi Adjoua", 100m)], Reason), CancellationToken.None);

        result.Error.Should().Be(CustomerErrors.ClientNotFound);
    }

    [Fact]
    public async Task Answers_not_found_when_the_client_sits_outside_the_agency_perimeter()
    {
        var client = await SeedLegalClientAsync(TenantId, AgencyId);
        // Perimeter restricted to another agency: NOT FOUND, never FORBIDDEN — a 403 would
        // confirm that this client id exists.
        var handler = BuildHandler(out _, accessibleAgencies: OtherAgencyId);

        var result = await handler.Handle(new DeclareBeneficialOwnersCommand(
            client.Id, [External("Kouassi Adjoua", 100m)], Reason), CancellationToken.None);

        result.Error.Should().Be(CustomerErrors.ClientNotFound);
    }

    [Fact]
    public async Task Cannot_declare_owners_on_a_client_of_another_tenant()
    {
        var foreign = await SeedLegalClientAsync(OtherTenantId, AgencyId);
        var handler = BuildHandler(out _);

        var result = await handler.Handle(new DeclareBeneficialOwnersCommand(
            foreign.Id, [External("Kouassi Adjoua", 100m)], Reason), CancellationToken.None);

        result.Error.Should().Be(CustomerErrors.ClientNotFound);
    }
}
