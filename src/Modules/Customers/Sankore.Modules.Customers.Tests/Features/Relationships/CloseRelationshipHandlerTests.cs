namespace Sankore.Modules.Customers.Tests.Features.Relationships;

using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using NSubstitute;
using Sankore.Modules.Customers.Domain;
using Sankore.Modules.Customers.Features.Relationships.AddRelationship;
using Sankore.Modules.Customers.Features.Relationships.CloseRelationship;
using Sankore.Modules.Customers.Infrastructure;
using Sankore.Modules.Customers.Tests.TestSupport;
using Sankore.Shared.Infrastructure.Messaging;
using Sankore.Shared.Kernel.Authorization;
using Xunit;

public sealed class CloseRelationshipHandlerTests
{
    private static readonly Guid TenantId = Guid.NewGuid();
    private static readonly Guid AgencyId = Guid.NewGuid();
    private static readonly Guid UserId = Guid.NewGuid();

    private readonly TestCustomersDbContextFactory _factory = new(TenantId);

    private CloseRelationshipHandler CreateHandler(
        CustomersDbContext db,
        IAgencyScopeProvider? scope = null)
        => new(db, TestDoubles.CurrentUser(TenantId, UserId), scope ?? TestDoubles.AgencyScope());

    private AddRelationshipHandler CreateAddHandler(CustomersDbContext db)
        => new(
            db,
            TestDoubles.CurrentUser(TenantId, UserId),
            TestDoubles.AgencyScope(),
            TestDoubles.Encryptor(),
            TestDoubles.Indexer(),
            Substitute.For<IEventPublisher>());

    [Fact]
    public async Task Closing_a_relationship_dates_its_valid_to_and_records_the_reason()
    {
        using var db = _factory.CreateContext();
        var client = await TestClientFactory.SeedIndividualAsync(db, TenantId, AgencyId);

        var added = await CreateAddHandler(db).Handle(
            new AddRelationshipCommand(
                client.Id, RelationshipType.Guarantor, null, "Fatou Diarra", null, null, null),
            CancellationToken.None);

        var before = DateTimeOffset.UtcNow;
        var result = await CreateHandler(db).Handle(
            new CloseRelationshipCommand(client.Id, added.Value.RelationshipId, "Caution levée"),
            CancellationToken.None);

        result.IsSuccess.Should().BeTrue();

        using var read = _factory.CreateContext();
        var stored = await read.ClientRelationships.SingleAsync(r => r.Id == added.Value.RelationshipId);

        // The row survives: a guarantor who backed a loan must stay visible long after
        // the link ended.
        stored.ValidTo.Should().NotBeNull();
        stored.ValidTo!.Value.Should().BeOnOrAfter(before);
        stored.CloseReason.Should().Be("Caution levée");
        stored.ExternalFullName.Should().Be("Fatou Diarra");
    }

    [Fact]
    public async Task Closing_one_side_of_a_spouse_link_closes_the_mirror_too()
    {
        using var db = _factory.CreateContext();
        var awa = await TestClientFactory.SeedIndividualAsync(db, TenantId, AgencyId);
        var bakary = await TestClientFactory.SeedIndividualAsync(
            db, TenantId, AgencyId, first: "Bakary", last: "Koné", clientNumber: "ABJ-2026-000002");

        var added = await CreateAddHandler(db).Handle(
            new AddRelationshipCommand(awa.Id, RelationshipType.Spouse, bakary.Id, null, null, null, null),
            CancellationToken.None);

        var result = await CreateHandler(db).Handle(
            new CloseRelationshipCommand(awa.Id, added.Value.RelationshipId, "Divorce"),
            CancellationToken.None);

        result.IsSuccess.Should().BeTrue();

        using var read = _factory.CreateContext();
        var rows = await read.ClientRelationships.ToListAsync();

        // Leaving the mirror open would show Bakary married to someone who is not
        // married to him.
        rows.Should().HaveCount(2);
        rows.Should().OnlyContain(r => r.ValidTo != null);
        rows.Should().OnlyContain(r => r.CloseReason == "Divorce");
    }

    [Fact]
    public async Task Closing_a_dependent_link_lowers_the_dependents_count()
    {
        using var db = _factory.CreateContext();
        var client = await TestClientFactory.SeedIndividualAsync(db, TenantId, AgencyId);
        var add = CreateAddHandler(db);

        var first = await add.Handle(
            new AddRelationshipCommand(client.Id, RelationshipType.Child, null, "Enfant Un", null, null, null),
            CancellationToken.None);

        await add.Handle(
            new AddRelationshipCommand(client.Id, RelationshipType.Dependent, null, "Neveu", null, null, null),
            CancellationToken.None);

        await CreateHandler(db).Handle(
            new CloseRelationshipCommand(client.Id, first.Value.RelationshipId, "Majeur autonome"),
            CancellationToken.None);

        using var read = _factory.CreateContext();
        (await read.Clients.SingleAsync(c => c.Id == client.Id)).DependentsCount.Should().Be(1);
    }

    [Fact]
    public async Task Closing_an_already_closed_relationship_stays_safe()
    {
        using var db = _factory.CreateContext();
        var client = await TestClientFactory.SeedIndividualAsync(db, TenantId, AgencyId);

        var added = await CreateAddHandler(db).Handle(
            new AddRelationshipCommand(client.Id, RelationshipType.Proxy, null, "Fatou Diarra", null, null, null),
            CancellationToken.None);

        var handler = CreateHandler(db);
        await handler.Handle(
            new CloseRelationshipCommand(client.Id, added.Value.RelationshipId, "Mandat révoqué"),
            CancellationToken.None);

        var again = await handler.Handle(
            new CloseRelationshipCommand(client.Id, added.Value.RelationshipId, "Autre motif"),
            CancellationToken.None);

        again.IsSuccess.Should().BeTrue();

        using var read = _factory.CreateContext();
        var stored = await read.ClientRelationships.SingleAsync(r => r.Id == added.Value.RelationshipId);

        // The first closure is the truth: a retry must not rewrite history.
        stored.CloseReason.Should().Be("Mandat révoqué");
    }

    [Fact]
    public async Task An_unknown_relationship_reads_as_not_found()
    {
        using var db = _factory.CreateContext();
        var client = await TestClientFactory.SeedIndividualAsync(db, TenantId, AgencyId);

        var result = await CreateHandler(db).Handle(
            new CloseRelationshipCommand(client.Id, Guid.NewGuid(), null), CancellationToken.None);

        result.Error.Should().Be(CustomerErrors.RelationshipNotFound);
    }

    [Fact]
    public async Task A_relationship_belonging_to_another_client_reads_as_not_found()
    {
        using var db = _factory.CreateContext();
        var awa = await TestClientFactory.SeedIndividualAsync(db, TenantId, AgencyId);
        var bakary = await TestClientFactory.SeedIndividualAsync(
            db, TenantId, AgencyId, first: "Bakary", last: "Koné", clientNumber: "ABJ-2026-000002");

        var added = await CreateAddHandler(db).Handle(
            new AddRelationshipCommand(bakary.Id, RelationshipType.Proxy, null, "Fatou Diarra", null, null, null),
            CancellationToken.None);

        var result = await CreateHandler(db).Handle(
            new CloseRelationshipCommand(awa.Id, added.Value.RelationshipId, null), CancellationToken.None);

        result.Error.Should().Be(CustomerErrors.RelationshipNotFound);
    }

    [Fact]
    public async Task An_archived_client_accepts_no_closure()
    {
        using var db = _factory.CreateContext();
        var client = await TestClientFactory.SeedIndividualAsync(db, TenantId, AgencyId);

        var added = await CreateAddHandler(db).Handle(
            new AddRelationshipCommand(client.Id, RelationshipType.Proxy, null, "Fatou Diarra", null, null, null),
            CancellationToken.None);

        using var mutate = _factory.CreateContext();
        var tracked = await mutate.Clients.AsTracking().SingleAsync(c => c.Id == client.Id);
        tracked.Archive("Dossier clos", UserId);
        await mutate.SaveChangesAsync();

        using var db2 = _factory.CreateContext();
        var result = await CreateHandler(db2).Handle(
            new CloseRelationshipCommand(client.Id, added.Value.RelationshipId, "Motif"),
            CancellationToken.None);

        result.Error.Should().Be(CustomerErrors.ClientReadOnly);
    }

    [Fact]
    public async Task A_client_outside_the_agency_perimeter_reads_as_not_found()
    {
        using var db = _factory.CreateContext();
        var client = await TestClientFactory.SeedIndividualAsync(db, TenantId, AgencyId);

        var added = await CreateAddHandler(db).Handle(
            new AddRelationshipCommand(client.Id, RelationshipType.Proxy, null, "Fatou Diarra", null, null, null),
            CancellationToken.None);

        var handler = CreateHandler(db, TestDoubles.AgencyScope(Guid.NewGuid()));

        var result = await handler.Handle(
            new CloseRelationshipCommand(client.Id, added.Value.RelationshipId, null), CancellationToken.None);

        result.Error.Should().Be(CustomerErrors.ClientNotFound);
    }
}
