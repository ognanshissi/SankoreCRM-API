namespace Sankore.Modules.Customers.Tests.Features.ContactPoints;

using FluentAssertions;
using Sankore.Modules.Customers.Domain;
using Sankore.Modules.Customers.Features.ContactPoints.ListContactPoints;
using Sankore.Modules.Customers.Infrastructure;
using Sankore.Modules.Customers.Tests.TestSupport;
using Sankore.Shared.Infrastructure.Crypto;
using Sankore.Shared.Kernel.Authorization;
using Xunit;

public sealed class ListContactPointsHandlerTests : IDisposable
{
    private static readonly Guid TenantId = Guid.NewGuid();
    private static readonly Guid AgencyId = Guid.NewGuid();
    private static readonly Guid UserId = Guid.NewGuid();

    private readonly TestCustomersDbContextFactory _factory = new(TenantId);
    private readonly IFieldEncryptor _encryptor = TestDoubles.Encryptor();
    private readonly IBlindIndexer _indexer = TestDoubles.Indexer();

    public void Dispose() => _factory.Dispose();

    private ListContactPointsHandler CreateHandler(
        CustomersDbContext db,
        IAgencyScopeProvider? scope = null)
        => new(db, TestDoubles.CurrentUser(TenantId, UserId), scope ?? TestDoubles.AgencyScope(), _encryptor);

    private void Add(Client client, ContactPointType type, string value, bool primary)
        => client.AddContactPoint(
            type,
            _encryptor.Encrypt(value)!,
            _indexer.Compute(
                type == ContactPointType.Phone ? BlindIndexPurpose.Phone
                : type == ContactPointType.Email ? BlindIndexPurpose.Email
                : BlindIndexPurpose.PostalAddress,
                value),
            null,
            primary,
            DateTimeOffset.UtcNow.AddDays(-5),
            UserId);

    [Fact]
    public async Task Values_are_returned_masked_and_never_in_clear()
    {
        using var db = _factory.CreateContext();
        var client = TestClientFactory.Individual(TenantId, AgencyId);
        Add(client, ContactPointType.Phone, "+22507080918", true);
        Add(client, ContactPointType.Email, "awa@example.com", true);
        Add(client, ContactPointType.Address, "Cocody Angré 7e Tranche", true);
        await TestClientFactory.SeedAsync(db, client);

        var result = await CreateHandler(db).Handle(
            new ListContactPointsQuery(client.Id, IncludeClosed: false), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().HaveCount(3);

        var rendered = string.Join('|', result.Value.Select(cp => cp.MaskedValue));
        rendered.Should().NotContain("07080918");
        rendered.Should().NotContain("awa@example.com");
        rendered.Should().NotContain("Angré");
        result.Value.Should().OnlyContain(cp => cp.MaskedValue.Contains('•'));
    }

    [Fact]
    public async Task Closed_contact_points_are_hidden_unless_include_closed_is_requested()
    {
        using var db = _factory.CreateContext();
        var client = TestClientFactory.Individual(TenantId, AgencyId);
        Add(client, ContactPointType.Phone, "0708091801", true);
        Add(client, ContactPointType.Phone, "0708091802", false);
        var closed = client.ContactPoints.Last();
        client.CloseContactPoint(closed.Id, UserId, DateTimeOffset.UtcNow);
        await TestClientFactory.SeedAsync(db, client);

        var active = await CreateHandler(db).Handle(
            new ListContactPointsQuery(client.Id, IncludeClosed: false), CancellationToken.None);

        var all = await CreateHandler(db).Handle(
            new ListContactPointsQuery(client.Id, IncludeClosed: true), CancellationToken.None);

        active.Value.Should().HaveCount(1);
        all.Value.Should().HaveCount(2);
        all.Value.Should().Contain(cp => cp.Id == closed.Id && !cp.IsActive && cp.ValidTo != null);
    }

    [Fact]
    public async Task A_contact_point_belonging_to_another_tenant_never_appears()
    {
        var otherTenant = Guid.NewGuid();

        using var db = _factory.CreateContext();
        var client = TestClientFactory.Individual(TenantId, AgencyId);
        Add(client, ContactPointType.Phone, "0708091801", true);
        await TestClientFactory.SeedAsync(db, client);

        // Same client id, another tenant: only the global query filter stands between
        // this row and the response.
        var foreign = ClientContactPoint.Create(
            otherTenant, client.Id, ContactPointType.Phone,
            _encryptor.Encrypt("0709000000")!,
            _indexer.Compute(BlindIndexPurpose.Phone, "0709000000"),
            null, false, DateTimeOffset.UtcNow, UserId);

        db.ClientContactPoints.Add(foreign);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var result = await CreateHandler(db).Handle(
            new ListContactPointsQuery(client.Id, IncludeClosed: true), CancellationToken.None);

        result.Value.Should().HaveCount(1);
        result.Value.Should().NotContain(cp => cp.Id == foreign.Id);
    }

    [Fact]
    public async Task A_client_outside_the_agency_perimeter_reads_as_not_found()
    {
        using var db = _factory.CreateContext();
        var client = await TestClientFactory.SeedIndividualAsync(db, TenantId, AgencyId);

        var handler = CreateHandler(db, TestDoubles.AgencyScope(Guid.NewGuid()));

        var result = await handler.Handle(
            new ListContactPointsQuery(client.Id, IncludeClosed: false), CancellationToken.None);

        result.Error.Should().Be(CustomerErrors.ClientNotFound);
    }
}
