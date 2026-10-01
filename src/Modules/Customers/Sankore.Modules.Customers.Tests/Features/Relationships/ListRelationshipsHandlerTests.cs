namespace Sankore.Modules.Customers.Tests.Features.Relationships;

using FluentAssertions;
using Sankore.Modules.Customers.Domain;
using Sankore.Modules.Customers.Features.Relationships.ListRelationships;
using Sankore.Modules.Customers.Infrastructure;
using Sankore.Modules.Customers.Tests.TestSupport;
using Sankore.Shared.Infrastructure.Crypto;
using Sankore.Shared.Kernel.Authorization;
using Xunit;

public sealed class ListRelationshipsHandlerTests : IDisposable
{
    private static readonly Guid TenantId = Guid.NewGuid();
    private static readonly Guid AgencyId = Guid.NewGuid();
    private static readonly Guid UserId = Guid.NewGuid();

    private readonly TestCustomersDbContextFactory _factory = new(TenantId);
    private readonly IFieldEncryptor _encryptor = TestDoubles.Encryptor();
    private readonly IBlindIndexer _indexer = TestDoubles.Indexer();

    public void Dispose() => _factory.Dispose();

    private ListRelationshipsHandler CreateHandler(
        CustomersDbContext db,
        IAgencyScopeProvider? scope = null)
        => new(db, TestDoubles.CurrentUser(TenantId, UserId), scope ?? TestDoubles.AgencyScope(), _encryptor);

    private ClientRelationship External(Guid tenantId, Guid clientId, string name, string? phone = null,
        DateOnly? dob = null, string? document = null, RelationshipType type = RelationshipType.Guarantor)
        => ClientRelationship.ToExternal(
            tenantId, clientId, type, name,
            phone is null ? null : _encryptor.Encrypt(phone),
            phone is null ? null : _indexer.Compute(BlindIndexPurpose.Phone, phone),
            dob,
            document is null ? null : _encryptor.Encrypt(document),
            DateTimeOffset.UtcNow.AddDays(-4),
            UserId);

    [Fact]
    public async Task An_external_person_data_is_returned_masked()
    {
        using var db = _factory.CreateContext();
        var client = await TestClientFactory.SeedIndividualAsync(db, TenantId, AgencyId);

        db.ClientRelationships.Add(External(
            TenantId, client.Id, "Fatou Diarra", "+22505443322", new DateOnly(1985, 3, 9), "CI123456789"));
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var result = await CreateHandler(db).Handle(
            new ListRelationshipsQuery(client.Id, IncludeClosed: false), CancellationToken.None);

        var dto = result.Value.Single();

        dto.ExternalFullName.Should().Be("Fatou Diarra");
        dto.MaskedExternalPhone.Should().NotContain("443322").And.Contain("•");
        dto.MaskedExternalDocumentNumber.Should().NotContain("123456789").And.Contain("•");
        dto.MaskedExternalDateOfBirth.Should().Be("••/••/1985");
    }

    [Fact]
    public async Task A_link_to_another_client_carries_that_client_identity()
    {
        using var db = _factory.CreateContext();
        var client = await TestClientFactory.SeedIndividualAsync(db, TenantId, AgencyId);
        var related = await TestClientFactory.SeedIndividualAsync(
            db, TenantId, AgencyId, first: "Bakary", last: "Koné", clientNumber: "ABJ-2026-000002");

        db.ClientRelationships.Add(ClientRelationship.ToClient(
            TenantId, client.Id, RelationshipType.Sibling, related.Id, DateTimeOffset.UtcNow, UserId));
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var result = await CreateHandler(db).Handle(
            new ListRelationshipsQuery(client.Id, IncludeClosed: false), CancellationToken.None);

        var dto = result.Value.Single();

        dto.RelatedClientId.Should().Be(related.Id);
        dto.RelatedClientNumber.Should().Be("ABJ-2026-000002");
        dto.RelatedClientDisplayName.Should().NotBeNullOrWhiteSpace();
        dto.MaskedExternalPhone.Should().BeNull();
    }

    [Fact]
    public async Task Closed_relationships_are_hidden_unless_include_closed_is_requested()
    {
        using var db = _factory.CreateContext();
        var client = await TestClientFactory.SeedIndividualAsync(db, TenantId, AgencyId);

        var active = External(TenantId, client.Id, "Fatou Diarra");
        var closed = External(TenantId, client.Id, "Ancien Garant");
        closed.Close("Caution levée", DateTimeOffset.UtcNow.AddDays(-1));

        db.ClientRelationships.AddRange(active, closed);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var visible = await CreateHandler(db).Handle(
            new ListRelationshipsQuery(client.Id, IncludeClosed: false), CancellationToken.None);

        var all = await CreateHandler(db).Handle(
            new ListRelationshipsQuery(client.Id, IncludeClosed: true), CancellationToken.None);

        visible.Value.Should().HaveCount(1);
        visible.Value.Single().Id.Should().Be(active.Id);
        all.Value.Should().HaveCount(2);
        all.Value.Should().Contain(r => r.Id == closed.Id && !r.IsActive && r.CloseReason == "Caution levée");
    }

    [Fact]
    public async Task A_relationship_belonging_to_another_tenant_never_appears()
    {
        var otherTenant = Guid.NewGuid();

        using var db = _factory.CreateContext();
        var client = await TestClientFactory.SeedIndividualAsync(db, TenantId, AgencyId);

        var mine = External(TenantId, client.Id, "Fatou Diarra");

        // Same client id, another tenant: only the global query filter keeps this row
        // out of the response.
        var foreign = External(otherTenant, client.Id, "Intrus Tenant B");

        db.ClientRelationships.AddRange(mine, foreign);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var result = await CreateHandler(db).Handle(
            new ListRelationshipsQuery(client.Id, IncludeClosed: true), CancellationToken.None);

        result.Value.Should().HaveCount(1);
        result.Value.Single().Id.Should().Be(mine.Id);
    }

    [Fact]
    public async Task A_client_outside_the_agency_perimeter_reads_as_not_found()
    {
        using var db = _factory.CreateContext();
        var client = await TestClientFactory.SeedIndividualAsync(db, TenantId, AgencyId);

        var handler = CreateHandler(db, TestDoubles.AgencyScope(Guid.NewGuid()));

        var result = await handler.Handle(
            new ListRelationshipsQuery(client.Id, IncludeClosed: false), CancellationToken.None);

        result.Error.Should().Be(CustomerErrors.ClientNotFound);
    }
}
