namespace Sankore.Modules.Customers.Tests.Features.ContactPoints;

using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Customers.Domain;
using Sankore.Modules.Customers.Features.ContactPoints.CloseContactPoint;
using Sankore.Modules.Customers.Infrastructure;
using Sankore.Modules.Customers.Tests.TestSupport;
using Sankore.Shared.Infrastructure.Crypto;
using Sankore.Shared.Kernel.Authorization;
using Xunit;

public sealed class CloseContactPointHandlerTests
{
    private static readonly Guid TenantId = Guid.NewGuid();
    private static readonly Guid AgencyId = Guid.NewGuid();
    private static readonly Guid UserId = Guid.NewGuid();

    private readonly TestCustomersDbContextFactory _factory = new(TenantId);
    private readonly IFieldEncryptor _encryptor = TestDoubles.Encryptor();
    private readonly IBlindIndexer _indexer = TestDoubles.Indexer();

    private CloseContactPointHandler CreateHandler(
        CustomersDbContext db,
        IAgencyScopeProvider? scope = null)
        => new(db, TestDoubles.CurrentUser(TenantId, UserId), scope ?? TestDoubles.AgencyScope());

    /// <summary>Builds a client already carrying the requested contact points.</summary>
    private async Task<Client> SeedClientWithAsync(
        CustomersDbContext db,
        params (ContactPointType Type, string Value, bool Primary)[] contacts)
    {
        var client = TestClientFactory.Individual(TenantId, AgencyId);

        foreach (var (type, value, primary) in contacts)
        {
            client.AddContactPoint(
                type,
                _encryptor.Encrypt(value)!,
                _indexer.Compute(
                    type == ContactPointType.Phone ? BlindIndexPurpose.Phone
                    : type == ContactPointType.Email ? BlindIndexPurpose.Email
                    : BlindIndexPurpose.PostalAddress,
                    value),
                null,
                primary,
                DateTimeOffset.UtcNow.AddDays(-10),
                UserId);
        }

        return await TestClientFactory.SeedAsync(db, client);
    }

    [Fact]
    public async Task Closing_a_contact_point_dates_its_valid_to_instead_of_deleting_the_row()
    {
        using var db = _factory.CreateContext();
        var client = await SeedClientWithAsync(
            db,
            (ContactPointType.Phone, "0708091801", true),
            (ContactPointType.Phone, "0708091802", false));

        var target = client.ContactPoints.Single(cp => _encryptor.Decrypt(cp.EncryptedValue) == "0708091802");

        var before = DateTimeOffset.UtcNow;
        var result = await CreateHandler(db).Handle(
            new CloseContactPointCommand(client.Id, target.Id), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();

        using var read = _factory.CreateContext();
        var stored = await read.ClientContactPoints.SingleAsync(cp => cp.Id == target.Id);

        // Still there, and still readable: a number once used to reach a client is
        // compliance evidence, not a row to drop.
        stored.ValidTo.Should().NotBeNull();
        stored.ValidTo!.Value.Should().BeOnOrAfter(before);
        stored.EncryptedValue.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task Closing_the_last_active_phone_is_refused()
    {
        using var db = _factory.CreateContext();
        var client = await SeedClientWithAsync(
            db,
            (ContactPointType.Phone, "0708091801", true),
            (ContactPointType.Email, "awa@example.com", true));

        var phone = client.ContactPoints.Single(cp => cp.Type == ContactPointType.Phone);

        var result = await CreateHandler(db).Handle(
            new CloseContactPointCommand(client.Id, phone.Id), CancellationToken.None);

        result.Error.Should().Be(CustomerErrors.LastPhoneRequired);

        using var read = _factory.CreateContext();
        (await read.ClientContactPoints.SingleAsync(cp => cp.Id == phone.Id)).ValidTo.Should().BeNull();
    }

    [Fact]
    public async Task Closing_the_last_email_is_allowed_because_only_a_phone_is_mandatory()
    {
        using var db = _factory.CreateContext();
        var client = await SeedClientWithAsync(
            db,
            (ContactPointType.Phone, "0708091801", true),
            (ContactPointType.Email, "awa@example.com", true));

        var email = client.ContactPoints.Single(cp => cp.Type == ContactPointType.Email);

        var result = await CreateHandler(db).Handle(
            new CloseContactPointCommand(client.Id, email.Id), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
    }

    [Fact]
    public async Task Closing_the_primary_promotes_a_remaining_active_contact_point_of_the_same_type()
    {
        using var db = _factory.CreateContext();
        var client = await SeedClientWithAsync(
            db,
            (ContactPointType.Phone, "0708091801", true),
            (ContactPointType.Phone, "0708091802", false));

        var primary = client.ContactPoints.Single(cp => cp.IsPrimary);

        var result = await CreateHandler(db).Handle(
            new CloseContactPointCommand(client.Id, primary.Id), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();

        using var read = _factory.CreateContext();
        var active = await read.ClientContactPoints
            .Where(cp => cp.ClientId == client.Id && cp.ValidTo == null)
            .ToListAsync();

        active.Should().HaveCount(1);
        active.Single().IsPrimary.Should().BeTrue();
    }

    [Fact]
    public async Task An_unknown_contact_point_reads_as_not_found()
    {
        using var db = _factory.CreateContext();
        var client = await SeedClientWithAsync(db, (ContactPointType.Phone, "0708091801", true));

        var result = await CreateHandler(db).Handle(
            new CloseContactPointCommand(client.Id, Guid.NewGuid()), CancellationToken.None);

        result.Error.Should().Be(CustomerErrors.ContactPointNotFound);
    }

    [Fact]
    public async Task An_archived_client_accepts_no_closure()
    {
        using var db = _factory.CreateContext();
        var client = TestClientFactory.Individual(TenantId, AgencyId);
        client.AddContactPoint(
            ContactPointType.Phone, _encryptor.Encrypt("0708091801")!,
            _indexer.Compute(BlindIndexPurpose.Phone, "0708091801"),
            null, true, DateTimeOffset.UtcNow, UserId);
        var contactPointId = client.ContactPoints.Single().Id;
        client.Archive("Dossier clos", UserId);
        await TestClientFactory.SeedAsync(db, client);

        var result = await CreateHandler(db).Handle(
            new CloseContactPointCommand(client.Id, contactPointId), CancellationToken.None);

        result.Error.Should().Be(CustomerErrors.ClientReadOnly);
    }

    [Fact]
    public async Task A_client_outside_the_agency_perimeter_reads_as_not_found()
    {
        using var db = _factory.CreateContext();
        var client = await SeedClientWithAsync(db, (ContactPointType.Phone, "0708091801", true));

        var handler = CreateHandler(db, TestDoubles.AgencyScope(Guid.NewGuid()));

        var result = await handler.Handle(
            new CloseContactPointCommand(client.Id, client.ContactPoints.Single().Id),
            CancellationToken.None);

        result.Error.Should().Be(CustomerErrors.ClientNotFound);
    }
}
