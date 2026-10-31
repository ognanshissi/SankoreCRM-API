namespace Sankore.Modules.Customers.Tests.Features.ContactPoints;

using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Customers.Domain;
using Sankore.Modules.Customers.Features.ContactPoints.PromoteContactPointToPrimary;
using Sankore.Modules.Customers.Infrastructure;
using Sankore.Modules.Customers.Tests.TestSupport;
using Sankore.Shared.Infrastructure.Crypto;
using Sankore.Shared.Kernel.Authorization;
using Xunit;

public sealed class PromoteContactPointToPrimaryHandlerTests : IDisposable
{
    private static readonly Guid TenantId = Guid.NewGuid();
    private static readonly Guid AgencyId = Guid.NewGuid();
    private static readonly Guid UserId = Guid.NewGuid();

    private readonly TestCustomersDbContextFactory _factory = new(TenantId);
    private readonly IFieldEncryptor _encryptor = TestDoubles.Encryptor();
    private readonly IBlindIndexer _indexer = TestDoubles.Indexer();

    public void Dispose() => _factory.Dispose();

    private PromoteContactPointToPrimaryHandler CreateHandler(
        CustomersDbContext db,
        IAgencyScopeProvider? scope = null)
        => new(db, TestDoubles.CurrentUser(TenantId, UserId), scope ?? TestDoubles.AgencyScope(),
               TestDoubles.ContactPointAnnouncer());

    private void Add(Client client, string value, bool primary)
        => client.AddContactPoint(
            ContactPointType.Phone,
            _encryptor.Encrypt(value)!,
            _indexer.Compute(BlindIndexPurpose.Phone, value),
            null, primary, DateTimeOffset.UtcNow.AddDays(-3), UserId);

    [Fact]
    public async Task Promoting_a_contact_point_demotes_the_previous_primary_of_the_same_type()
    {
        using var db = _factory.CreateContext();
        var client = TestClientFactory.Individual(TenantId, AgencyId);
        Add(client, "0708091801", true);
        Add(client, "0708091802", false);
        var challenger = client.ContactPoints.Last();
        await TestClientFactory.SeedAsync(db, client);

        var result = await CreateHandler(db).Handle(
            new PromoteContactPointToPrimaryCommand(client.Id, challenger.Id), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();

        using var read = _factory.CreateContext();
        var phones = await read.ClientContactPoints
            .Where(cp => cp.ClientId == client.Id && cp.ValidTo == null)
            .ToListAsync();

        phones.Should().HaveCount(2);
        phones.Single(cp => cp.IsPrimary).Id.Should().Be(challenger.Id);
    }

    [Fact]
    public async Task The_former_primary_is_demoted_in_an_earlier_write_than_the_promotion()
    {
        // PostgreSQL checks ux_client_contact_points_primary row by row and InMemory checks nothing:
        // what is pinned is that no single save both demotes and promotes a primary of one type.
        using var db = _factory.CreateContext();
        var client = TestClientFactory.Individual(TenantId, AgencyId);
        Add(client, "0708091801", true);
        Add(client, "0708091802", false);
        var challenger = client.ContactPoints.Last();
        await TestClientFactory.SeedAsync(db, client);

        var rounds = new List<(bool Demotes, bool Promotes)>();
        db.SavingChanges += (_, _) =>
        {
            var changes = db.ChangeTracker.Entries<ClientContactPoint>()
                .Where(e => e.State == EntityState.Modified && e.Property(cp => cp.IsPrimary).IsModified)
                .ToList();
            rounds.Add((
                changes.Any(e => e.Property(cp => cp.IsPrimary).OriginalValue && !e.Entity.IsPrimary),
                changes.Any(e => !e.Property(cp => cp.IsPrimary).OriginalValue && e.Entity.IsPrimary)));
        };

        var result = await CreateHandler(db).Handle(
            new PromoteContactPointToPrimaryCommand(client.Id, challenger.Id), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        rounds.Should().NotContain(r => r.Demotes && r.Promotes);
        rounds.Should().Contain(r => r.Promotes);
    }

    [Fact]
    public async Task A_closed_contact_point_cannot_be_promoted()
    {
        using var db = _factory.CreateContext();
        var client = TestClientFactory.Individual(TenantId, AgencyId);
        Add(client, "0708091801", true);
        Add(client, "0708091802", false);
        var closed = client.ContactPoints.Last();
        client.CloseContactPoint(closed.Id, UserId, DateTimeOffset.UtcNow);
        await TestClientFactory.SeedAsync(db, client);

        var result = await CreateHandler(db).Handle(
            new PromoteContactPointToPrimaryCommand(client.Id, closed.Id), CancellationToken.None);

        result.Error.Should().Be(CustomerErrors.ContactPointNotFound);
    }

    [Fact]
    public async Task An_archived_client_accepts_no_promotion()
    {
        using var db = _factory.CreateContext();
        var client = TestClientFactory.Individual(TenantId, AgencyId);
        Add(client, "0708091801", true);
        var contactPointId = client.ContactPoints.Single().Id;
        client.Archive("Dossier clos", UserId);
        await TestClientFactory.SeedAsync(db, client);

        var result = await CreateHandler(db).Handle(
            new PromoteContactPointToPrimaryCommand(client.Id, contactPointId), CancellationToken.None);

        result.Error.Should().Be(CustomerErrors.ClientReadOnly);
    }

    [Fact]
    public async Task A_client_outside_the_agency_perimeter_reads_as_not_found()
    {
        using var db = _factory.CreateContext();
        var client = TestClientFactory.Individual(TenantId, AgencyId);
        Add(client, "0708091801", true);
        var contactPointId = client.ContactPoints.Single().Id;
        await TestClientFactory.SeedAsync(db, client);

        var handler = CreateHandler(db, TestDoubles.AgencyScope(Guid.NewGuid()));

        var result = await handler.Handle(
            new PromoteContactPointToPrimaryCommand(client.Id, contactPointId), CancellationToken.None);

        result.Error.Should().Be(CustomerErrors.ClientNotFound);
    }
}
