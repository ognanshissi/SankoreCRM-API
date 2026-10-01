namespace Sankore.Modules.Customers.Tests.Features.Relationships;

using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using NSubstitute;
using Sankore.Modules.Customers.Domain;
using Sankore.Modules.Customers.Features.Relationships.AddRelationship;
using Sankore.Modules.Customers.Infrastructure;
using Sankore.Modules.Customers.PublicApi.Events;
using Sankore.Modules.Customers.Tests.TestSupport;
using Sankore.Shared.Infrastructure.Crypto;
using Sankore.Shared.Infrastructure.Messaging;
using Sankore.Shared.Kernel.Authorization;
using Xunit;

public sealed class AddRelationshipHandlerTests : IDisposable
{
    private static readonly Guid TenantId = Guid.NewGuid();
    private static readonly Guid AgencyId = Guid.NewGuid();
    private static readonly Guid UserId = Guid.NewGuid();

    private readonly TestCustomersDbContextFactory _factory = new(TenantId);
    private readonly IFieldEncryptor _encryptor = TestDoubles.Encryptor();
    private readonly IBlindIndexer _indexer = TestDoubles.Indexer();
    private readonly IEventPublisher _publisher = Substitute.For<IEventPublisher>();

    public void Dispose() => _factory.Dispose();

    private AddRelationshipHandler CreateHandler(
        CustomersDbContext db,
        IAgencyScopeProvider? scope = null)
        => new(
            db,
            TestDoubles.CurrentUser(TenantId, UserId),
            scope ?? TestDoubles.AgencyScope(),
            _encryptor,
            _indexer,
            _publisher);

    [Fact]
    public async Task A_relationship_can_target_another_client_of_the_tenant()
    {
        using var db = _factory.CreateContext();
        var client = await TestClientFactory.SeedIndividualAsync(db, TenantId, AgencyId);
        var guarantor = await TestClientFactory.SeedIndividualAsync(
            db, TenantId, AgencyId, first: "Bakary", last: "Koné", clientNumber: "ABJ-2026-000002");

        var result = await CreateHandler(db).Handle(
            new AddRelationshipCommand(
                client.Id, RelationshipType.Proxy, guarantor.Id, null, null, null, null),
            CancellationToken.None);

        result.IsSuccess.Should().BeTrue();

        using var read = _factory.CreateContext();
        var stored = await read.ClientRelationships.SingleAsync(r => r.Id == result.Value.RelationshipId);

        stored.RelatedClientId.Should().Be(guarantor.Id);
        stored.ExternalFullName.Should().BeNull();
        stored.ValidFrom.Should().BeCloseTo(DateTimeOffset.UtcNow, TimeSpan.FromMinutes(1));
        stored.ValidTo.Should().BeNull();
    }

    [Fact]
    public async Task A_relationship_can_target_a_person_who_is_not_a_client_and_encrypts_their_data()
    {
        using var db = _factory.CreateContext();
        var client = await TestClientFactory.SeedIndividualAsync(db, TenantId, AgencyId);

        var result = await CreateHandler(db).Handle(
            new AddRelationshipCommand(
                client.Id, RelationshipType.Guarantor, null,
                "Fatou Diarra", "+225 05 44 33 22", new DateOnly(1985, 3, 9), "CI123456789"),
            CancellationToken.None);

        result.IsSuccess.Should().BeTrue();

        using var read = _factory.CreateContext();
        var stored = await read.ClientRelationships.SingleAsync(r => r.Id == result.Value.RelationshipId);

        stored.ExternalFullName.Should().Be("Fatou Diarra");
        stored.RelatedClientId.Should().BeNull();

        // The third party's phone and document never sit in clear, and the phone stays
        // searchable through its blind index alone.
        stored.ExternalPhoneEncrypted.Should().NotBe("+225 05 44 33 22");
        _encryptor.Decrypt(stored.ExternalPhoneEncrypted).Should().Be("+225 05 44 33 22");
        stored.ExternalPhoneBlindIndex.Should()
            .Be(_indexer.Compute(BlindIndexPurpose.Phone, "+225 05 44 33 22"));
        stored.ExternalPhoneBlindIndex.Should().NotContain("44 33");
        stored.EncryptedExternalDocumentNumber.Should().NotBe("CI123456789");
        _encryptor.Decrypt(stored.EncryptedExternalDocumentNumber).Should().Be("CI123456789");
        stored.ExternalDateOfBirth.Should().Be(new DateOnly(1985, 3, 9));
    }

    [Fact]
    public async Task Relating_a_client_to_itself_is_refused()
    {
        using var db = _factory.CreateContext();
        var client = await TestClientFactory.SeedIndividualAsync(db, TenantId, AgencyId);

        var result = await CreateHandler(db).Handle(
            new AddRelationshipCommand(
                client.Id, RelationshipType.Spouse, client.Id, null, null, null, null),
            CancellationToken.None);

        result.Error.Should().Be(CustomerErrors.SelfRelationshipForbidden);

        using var read = _factory.CreateContext();
        (await read.ClientRelationships.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task A_spouse_link_between_two_clients_creates_the_mirror_relationship()
    {
        using var db = _factory.CreateContext();
        var awa = await TestClientFactory.SeedIndividualAsync(db, TenantId, AgencyId);
        var bakary = await TestClientFactory.SeedIndividualAsync(
            db, TenantId, AgencyId, first: "Bakary", last: "Koné", clientNumber: "ABJ-2026-000002");

        var result = await CreateHandler(db).Handle(
            new AddRelationshipCommand(awa.Id, RelationshipType.Spouse, bakary.Id, null, null, null, null),
            CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.ReciprocalRelationshipId.Should().NotBeNull();

        using var read = _factory.CreateContext();
        var rows = await read.ClientRelationships.ToListAsync();

        rows.Should().HaveCount(2);

        var forward = rows.Single(r => r.ClientId == awa.Id);
        var mirror = rows.Single(r => r.ClientId == bakary.Id);

        // Both ends see the marriage, and each row points at the other so a later
        // closure can cascade.
        forward.RelatedClientId.Should().Be(bakary.Id);
        mirror.RelatedClientId.Should().Be(awa.Id);
        forward.ReciprocalRelationshipId.Should().Be(mirror.Id);
        mirror.ReciprocalRelationshipId.Should().Be(forward.Id);
        mirror.Type.Should().Be(RelationshipType.Spouse);
    }

    [Fact]
    public async Task A_spouse_link_to_an_external_person_creates_no_mirror()
    {
        using var db = _factory.CreateContext();
        var client = await TestClientFactory.SeedIndividualAsync(db, TenantId, AgencyId);

        var result = await CreateHandler(db).Handle(
            new AddRelationshipCommand(
                client.Id, RelationshipType.Spouse, null, "Fatou Diarra", null, null, null),
            CancellationToken.None);

        result.Value.ReciprocalRelationshipId.Should().BeNull();

        using var read = _factory.CreateContext();
        (await read.ClientRelationships.CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task A_guarantor_link_publishes_the_integration_event()
    {
        using var db = _factory.CreateContext();
        var client = await TestClientFactory.SeedIndividualAsync(db, TenantId, AgencyId);
        var guarantor = await TestClientFactory.SeedIndividualAsync(
            db, TenantId, AgencyId, first: "Bakary", last: "Koné", clientNumber: "ABJ-2026-000002");

        var result = await CreateHandler(db).Handle(
            new AddRelationshipCommand(
                client.Id, RelationshipType.Guarantor, guarantor.Id, null, null, null, null),
            CancellationToken.None);

        result.IsSuccess.Should().BeTrue();

        await _publisher.Received(1).PublishAsync(
            Arg.Is<GuarantorLinkedEvent>(e =>
                e.TenantId == TenantId &&
                e.ClientId == client.Id &&
                e.GuarantorClientId == guarantor.Id &&
                e.GuarantorExternalName == null &&
                e.ActorUserId == UserId),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_non_guarantor_link_publishes_nothing()
    {
        using var db = _factory.CreateContext();
        var client = await TestClientFactory.SeedIndividualAsync(db, TenantId, AgencyId);

        await CreateHandler(db).Handle(
            new AddRelationshipCommand(
                client.Id, RelationshipType.Sibling, null, "Fatou Diarra", null, null, null),
            CancellationToken.None);

        await _publisher.DidNotReceive().PublishAsync(
            Arg.Any<GuarantorLinkedEvent>(), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(RelationshipType.Child)]
    [InlineData(RelationshipType.Dependent)]
    public async Task Child_and_dependent_links_raise_the_dependents_count(RelationshipType type)
    {
        using var db = _factory.CreateContext();
        var client = await TestClientFactory.SeedIndividualAsync(db, TenantId, AgencyId);
        var handler = CreateHandler(db);

        await handler.Handle(
            new AddRelationshipCommand(client.Id, type, null, "Enfant Un", null, null, null),
            CancellationToken.None);

        var second = await handler.Handle(
            new AddRelationshipCommand(client.Id, type, null, "Enfant Deux", null, null, null),
            CancellationToken.None);

        second.Value.DependentsCount.Should().Be(2);

        using var read = _factory.CreateContext();
        (await read.Clients.SingleAsync(c => c.Id == client.Id)).DependentsCount.Should().Be(2);
    }

    [Fact]
    public async Task Other_relationship_types_leave_the_dependents_count_untouched()
    {
        using var db = _factory.CreateContext();
        var client = await TestClientFactory.SeedIndividualAsync(db, TenantId, AgencyId);

        var result = await CreateHandler(db).Handle(
            new AddRelationshipCommand(
                client.Id, RelationshipType.Guarantor, null, "Fatou Diarra", null, null, null),
            CancellationToken.None);

        result.Value.DependentsCount.Should().Be(0);

        using var read = _factory.CreateContext();
        (await read.Clients.SingleAsync(c => c.Id == client.Id)).DependentsCount.Should().Be(0);
    }

    [Fact]
    public async Task A_client_outside_the_agency_perimeter_reads_as_not_found()
    {
        using var db = _factory.CreateContext();
        var client = await TestClientFactory.SeedIndividualAsync(db, TenantId, AgencyId);

        var handler = CreateHandler(db, TestDoubles.AgencyScope(Guid.NewGuid()));

        var result = await handler.Handle(
            new AddRelationshipCommand(
                client.Id, RelationshipType.Sibling, null, "Fatou Diarra", null, null, null),
            CancellationToken.None);

        result.Error.Should().Be(CustomerErrors.ClientNotFound);
    }

    [Fact]
    public async Task A_related_client_outside_the_agency_perimeter_reads_as_not_found()
    {
        var otherAgency = Guid.NewGuid();

        using var db = _factory.CreateContext();
        var client = await TestClientFactory.SeedIndividualAsync(db, TenantId, AgencyId);
        var elsewhere = await TestClientFactory.SeedIndividualAsync(
            db, TenantId, otherAgency, first: "Bakary", last: "Koné", clientNumber: "BKE-2026-000001");

        // The caller sees only its own agency: the other client must read as missing,
        // never as forbidden.
        var handler = CreateHandler(db, TestDoubles.AgencyScope(AgencyId));

        var result = await handler.Handle(
            new AddRelationshipCommand(client.Id, RelationshipType.Spouse, elsewhere.Id, null, null, null, null),
            CancellationToken.None);

        result.Error.Should().Be(CustomerErrors.ClientNotFound);
    }

    [Fact]
    public async Task An_archived_client_accepts_no_new_relationship()
    {
        using var db = _factory.CreateContext();
        var client = TestClientFactory.Individual(TenantId, AgencyId);
        client.Archive("Dossier clos", UserId);
        await TestClientFactory.SeedAsync(db, client);

        var result = await CreateHandler(db).Handle(
            new AddRelationshipCommand(
                client.Id, RelationshipType.Sibling, null, "Fatou Diarra", null, null, null),
            CancellationToken.None);

        result.Error.Should().Be(CustomerErrors.ClientReadOnly);
    }

    [Fact]
    public async Task A_client_of_another_tenant_is_invisible()
    {
        var otherTenant = Guid.NewGuid();

        using var db = _factory.CreateContext();
        var foreignClient = TestClientFactory.Individual(otherTenant, AgencyId, clientNumber: "XXX-2026-000009");
        db.Clients.Add(foreignClient);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var result = await CreateHandler(db).Handle(
            new AddRelationshipCommand(
                foreignClient.Id, RelationshipType.Sibling, null, "Fatou Diarra", null, null, null),
            CancellationToken.None);

        result.Error.Should().Be(CustomerErrors.ClientNotFound);
    }
}
