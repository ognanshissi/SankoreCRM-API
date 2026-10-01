namespace Sankore.Modules.Customers.Tests.Features.ContactPoints;

using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Customers.Domain;
using Sankore.Modules.Customers.Features.ContactPoints.AddContactPoint;
using Sankore.Modules.Customers.Tests.TestSupport;
using Sankore.Shared.Infrastructure.Crypto;
using Sankore.Shared.Kernel.Authorization;
using Xunit;

public sealed class AddContactPointHandlerTests : IDisposable
{
    private static readonly Guid TenantId = Guid.NewGuid();
    private static readonly Guid AgencyId = Guid.NewGuid();
    private static readonly Guid UserId = Guid.NewGuid();

    private readonly TestCustomersDbContextFactory _factory = new(TenantId);
    private readonly IFieldEncryptor _encryptor = TestDoubles.Encryptor();
    private readonly IBlindIndexer _indexer = TestDoubles.Indexer();

    public void Dispose() => _factory.Dispose();

    private AddContactPointHandler CreateHandler(
        Sankore.Modules.Customers.Infrastructure.CustomersDbContext db,
        IAgencyScopeProvider? scope = null)
        => new(
            db,
            TestDoubles.CurrentUser(TenantId, UserId),
            scope ?? TestDoubles.AgencyScope(),
            _encryptor,
            _indexer);

    [Fact]
    public async Task Adding_a_phone_stores_its_ciphertext_blind_index_and_valid_from_date()
    {
        using var db = _factory.CreateContext();
        var client = await TestClientFactory.SeedIndividualAsync(db, TenantId, AgencyId);

        var before = DateTimeOffset.UtcNow;
        var result = await CreateHandler(db).Handle(
            new AddContactPointCommand(client.Id, ContactPointType.Phone, "+225 07 08 09 18", "Mobile", false),
            CancellationToken.None);

        result.IsSuccess.Should().BeTrue();

        using var read = _factory.CreateContext();
        var stored = await read.ClientContactPoints.SingleAsync(cp => cp.ClientId == client.Id);

        // The clear value never reaches the column, and the searchable form is a
        // one-way index — not the number itself.
        stored.EncryptedValue.Should().NotBe("+225 07 08 09 18");
        _encryptor.Decrypt(stored.EncryptedValue).Should().Be("+225 07 08 09 18");
        stored.BlindIndex.Should().Be(_indexer.Compute(BlindIndexPurpose.Phone, "+225 07 08 09 18"));
        stored.ValidFrom.Should().BeOnOrAfter(before);
        stored.ValidTo.Should().BeNull();
        stored.Label.Should().Be("Mobile");
    }

    [Fact]
    public async Task The_response_masks_the_value_it_just_stored()
    {
        using var db = _factory.CreateContext();
        var client = await TestClientFactory.SeedIndividualAsync(db, TenantId, AgencyId);

        var result = await CreateHandler(db).Handle(
            new AddContactPointCommand(client.Id, ContactPointType.Email, "awa@example.com", null, false),
            CancellationToken.None);

        result.Value.MaskedValue.Should().NotContain("awa");
        result.Value.MaskedValue.Should().Contain("•");
    }

    [Fact]
    public async Task The_first_contact_point_of_a_type_becomes_primary_even_when_not_requested()
    {
        using var db = _factory.CreateContext();
        var client = await TestClientFactory.SeedIndividualAsync(db, TenantId, AgencyId);

        var result = await CreateHandler(db).Handle(
            new AddContactPointCommand(client.Id, ContactPointType.Phone, "0708091801", null, false),
            CancellationToken.None);

        result.Value.IsPrimary.Should().BeTrue();
    }

    [Fact]
    public async Task Only_one_primary_stays_active_per_contact_point_type()
    {
        using var db = _factory.CreateContext();
        var client = await TestClientFactory.SeedIndividualAsync(db, TenantId, AgencyId);
        var handler = CreateHandler(db);

        await handler.Handle(
            new AddContactPointCommand(client.Id, ContactPointType.Phone, "0708091801", null, true),
            CancellationToken.None);

        await handler.Handle(
            new AddContactPointCommand(client.Id, ContactPointType.Phone, "0708091802", null, true),
            CancellationToken.None);

        // An email primary must not be affected by a phone promotion: the invariant is
        // one primary per TYPE, not one per client.
        await handler.Handle(
            new AddContactPointCommand(client.Id, ContactPointType.Email, "awa@example.com", null, true),
            CancellationToken.None);

        using var read = _factory.CreateContext();
        var active = await read.ClientContactPoints
            .Where(cp => cp.ClientId == client.Id && cp.ValidTo == null)
            .ToListAsync();

        active.Should().HaveCount(3);
        active.Count(cp => cp.Type == ContactPointType.Phone && cp.IsPrimary).Should().Be(1);
        active.Single(cp => cp.Type == ContactPointType.Email).IsPrimary.Should().BeTrue();
    }

    [Fact]
    public async Task Adding_a_value_already_on_file_returns_the_existing_contact_point()
    {
        using var db = _factory.CreateContext();
        var client = await TestClientFactory.SeedIndividualAsync(db, TenantId, AgencyId);
        var handler = CreateHandler(db);

        var first = await handler.Handle(
            new AddContactPointCommand(client.Id, ContactPointType.Phone, "+225 07 08 09 18 01", null, false),
            CancellationToken.None);

        // Same number, typed differently: normalization in the blind indexer makes the
        // two collide, so this is the same contact point, not a second one.
        var second = await handler.Handle(
            new AddContactPointCommand(client.Id, ContactPointType.Phone, "+225.07.08.09.18.01", "Mobile", false),
            CancellationToken.None);

        second.IsSuccess.Should().BeTrue();
        second.Value.AlreadyExisted.Should().BeTrue();
        second.Value.ContactPointId.Should().Be(first.Value.ContactPointId);

        using var read = _factory.CreateContext();
        (await read.ClientContactPoints.CountAsync(cp => cp.ClientId == client.Id)).Should().Be(1);
    }

    [Fact]
    public async Task Re_adding_an_existing_value_as_primary_promotes_it_without_duplicating_it()
    {
        using var db = _factory.CreateContext();
        var client = await TestClientFactory.SeedIndividualAsync(db, TenantId, AgencyId);
        var handler = CreateHandler(db);

        var mobile = await handler.Handle(
            new AddContactPointCommand(client.Id, ContactPointType.Phone, "0708091801", null, false),
            CancellationToken.None);

        await handler.Handle(
            new AddContactPointCommand(client.Id, ContactPointType.Phone, "0708091802", null, true),
            CancellationToken.None);

        var again = await handler.Handle(
            new AddContactPointCommand(client.Id, ContactPointType.Phone, "0708091801", null, true),
            CancellationToken.None);

        again.Value.AlreadyExisted.Should().BeTrue();
        again.Value.ContactPointId.Should().Be(mobile.Value.ContactPointId);

        using var read = _factory.CreateContext();
        var phones = await read.ClientContactPoints
            .Where(cp => cp.ClientId == client.Id && cp.Type == ContactPointType.Phone)
            .ToListAsync();

        phones.Should().HaveCount(2);
        phones.Single(cp => cp.IsPrimary).Id.Should().Be(mobile.Value.ContactPointId);
    }

    [Fact]
    public async Task A_client_outside_the_agency_perimeter_reads_as_not_found()
    {
        using var db = _factory.CreateContext();
        var client = await TestClientFactory.SeedIndividualAsync(db, TenantId, AgencyId);

        // The caller only reaches another agency: a 403 would confirm the record exists.
        var handler = CreateHandler(db, TestDoubles.AgencyScope(Guid.NewGuid()));

        var result = await handler.Handle(
            new AddContactPointCommand(client.Id, ContactPointType.Phone, "0708091801", null, false),
            CancellationToken.None);

        result.Error.Should().Be(CustomerErrors.ClientNotFound);
    }

    [Fact]
    public async Task An_unknown_client_reads_as_not_found()
    {
        using var db = _factory.CreateContext();

        var result = await CreateHandler(db).Handle(
            new AddContactPointCommand(Guid.NewGuid(), ContactPointType.Phone, "0708091801", null, false),
            CancellationToken.None);

        result.Error.Should().Be(CustomerErrors.ClientNotFound);
    }

    [Fact]
    public async Task An_archived_client_accepts_no_new_contact_point()
    {
        using var db = _factory.CreateContext();
        var client = TestClientFactory.Individual(TenantId, AgencyId);
        client.Archive("Dossier clos", UserId);
        await TestClientFactory.SeedAsync(db, client);

        var result = await CreateHandler(db).Handle(
            new AddContactPointCommand(client.Id, ContactPointType.Phone, "0708091801", null, false),
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
            new AddContactPointCommand(foreignClient.Id, ContactPointType.Phone, "0708091801", null, false),
            CancellationToken.None);

        result.Error.Should().Be(CustomerErrors.ClientNotFound);
    }
}
