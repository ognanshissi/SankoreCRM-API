namespace Sankore.Modules.Customers.Tests.Features.Clients;

using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Customers.Domain;
using Sankore.Modules.Customers.Features.Clients.CreateIndividualClient;
using Sankore.Modules.Customers.Features.Clients.GetClient;
using Sankore.Modules.Customers.Features.Clients.SearchClients;
using Sankore.Modules.Customers.Features.Clients.Shared;
using Sankore.Modules.Customers.Features.Clients.UpdateClient;
using Sankore.Modules.Customers.Infrastructure;
using Sankore.Modules.Customers.Tests.TestSupport;
using Sankore.Shared.Infrastructure.Crypto;
using Sankore.Shared.Infrastructure.Outbox;
using Sankore.Shared.Kernel;
using Xunit;

/// <summary>
/// Cross-tenant isolation of the <c>clients</c> zone.
///
/// The rows of tenant B are written THROUGH a tenant-A context on purpose: EF's global
/// query filters apply to reads, not to inserts, so this seeds a genuine foreign-tenant row
/// into the same physical store — which is exactly the situation a shared PostgreSQL
/// database creates. Every assertion below then checks that tenant A cannot see it, by any
/// route: detail, search, name prefix, and — the one that matters most — equality on a
/// BLIND INDEX, where a missing tenant predicate would otherwise be a perfect oracle for
/// "does this person bank with another institution on this platform?".
/// </summary>
public sealed class ClientsTenantIsolationTests : IDisposable
{
    private static readonly Guid TenantA = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid TenantB = Guid.Parse("99999999-9999-9999-9999-999999999999");
    private static readonly Guid AgencyId = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid ActorId = Guid.Parse("33333333-3333-3333-3333-333333333333");

    private const string DocumentNumber = "CI0012345642";
    private const string PhoneNumber = "+225 07 08 09 18";

    private readonly TestCustomersDbContextFactory _factory = new(TenantA);

    public void Dispose() => _factory.Dispose();

    [Fact]
    public async Task A_client_of_another_tenant_is_invisible_to_the_detail_endpoint()
    {
        await using var seed = _factory.CreateContext();
        var foreignClient = await SeedForeignClientAsync(seed);

        await using var db = _factory.CreateContext();
        var handler = new GetClientHandler(
            db,
            TestDoubles.CurrentUser(TenantA, ActorId),
            TestDoubles.AgencyScope(),
            TestDoubles.Encryptor());

        var result = await handler.Handle(new GetClientQuery(foreignClient.Id), CancellationToken.None);

        result.Error.Should().Be(CustomerErrors.ClientNotFound);
    }

    [Fact]
    public async Task A_client_of_another_tenant_never_appears_in_a_search()
    {
        await using var seed = _factory.CreateContext();
        await SeedForeignClientAsync(seed);
        await TestClientFactory.SeedIndividualAsync(
            seed, TenantA, AgencyId, first: "Awa", last: "Ouattara", clientNumber: "ABJ-2026-000500");

        await using var db = _factory.CreateContext();
        var handler = BuildSearchHandler(db);

        var all = await handler.Handle(SearchAll(), CancellationToken.None);
        all.Value.TotalCount.Should().Be(1);
        all.Value.Items.Should().ContainSingle()
            .Which.ClientNumber.Should().Be("ABJ-2026-000500");

        // Even by the foreign client's own name.
        var byName = await handler.Handle(SearchAll() with { Name = "Traore" }, CancellationToken.None);
        byName.Value.Items.Should().BeEmpty();
    }

    [Fact]
    public async Task A_client_of_another_tenant_is_not_reachable_by_blind_index_search()
    {
        await using var seed = _factory.CreateContext();
        await SeedForeignClientAsync(seed);

        await using var db = _factory.CreateContext();
        var handler = BuildSearchHandler(db);

        var byDocument = await handler.Handle(
            SearchAll() with { IdentityDocumentNumber = DocumentNumber }, CancellationToken.None);
        byDocument.Value.Items.Should().BeEmpty();

        var byPhone = await handler.Handle(
            SearchAll() with { Phone = PhoneNumber }, CancellationToken.None);
        byPhone.Value.Items.Should().BeEmpty();
    }

    [Fact]
    public async Task The_duplicate_probe_does_not_leak_another_tenants_client()
    {
        await using var seed = _factory.CreateContext();
        await SeedForeignClientAsync(seed);

        await using var db = _factory.CreateContext();
        var probe = new DuplicateProbe(db);
        var indexer = TestDoubles.Indexer();

        // The probe runs with IgnoreQueryFilters (it is also called from Hangfire jobs and
        // from lead conversion), so its EXPLICIT TenantId predicate is the only thing
        // standing between the two tenants. This is that guard's regression test.
        var documentHit = await probe.FindByIdentityDocumentAsync(
            TenantA,
            indexer.Compute(BlindIndexPurpose.IdentityDocument, DocumentNumber),
            excludeClientId: null,
            CancellationToken.None);

        documentHit.Should().BeNull();

        var phoneHits = await probe.FindActiveByPhoneAsync(
            TenantA,
            indexer.Compute(BlindIndexPurpose.Phone, PhoneNumber),
            excludeClientId: null,
            CancellationToken.None);

        phoneHits.Should().BeEmpty();

        // Same probe, the owning tenant: the row IS there, so the emptiness above is
        // isolation and not a broken query.
        var ownerHit = await probe.FindByIdentityDocumentAsync(
            TenantB,
            indexer.Compute(BlindIndexPurpose.IdentityDocument, DocumentNumber),
            excludeClientId: null,
            CancellationToken.None);

        ownerHit.Should().NotBeNull();
    }

    [Fact]
    public async Task Creating_a_client_is_not_blocked_by_another_tenants_identical_document()
    {
        await using var seed = _factory.CreateContext();
        await SeedForeignClientAsync(seed);

        await using var db = _factory.CreateContext();
        var encryptor = TestDoubles.Encryptor();

        var handler = new CreateIndividualClientHandler(
            db,
            TestDoubles.CurrentUser(TenantA, ActorId),
            TestDoubles.Settings(TenantA),
            new DuplicateProbe(db),
            ClientsTestHarness.AgencyDirectory(TenantA, AgencyId),
            ClientsTestHarness.ClientNumbers(),
            encryptor,
            TestDoubles.Indexer(),
            ClientsTestHarness.PhoneticKeys(),
            new OutboxEventPublisher<CustomersDbContext>(db));

        var result = await handler.Handle(
            new CreateIndividualClientCommand(
                AgencyId: AgencyId,
                AdvisorUserId: null,
                FirstName: "Awa",
                LastName: "Traoré",
                MaidenName: null,
                Gender: Gender.Female,
                DateOfBirth: new DateOnly(1990, 4, 12),
                BirthPlace: "Abidjan",
                Nationality: "CI",
                MaritalStatus: null,
                FatherName: null,
                MotherName: null,
                Profession: null,
                Employer: null,
                DeclaredIncome: null,
                DeclaredIncomeCurrency: null,
                PreferredLanguage: "fr",
                IdentityDocumentType: IdentityDocumentType.NationalIdCard,
                IdentityDocumentNumber: DocumentNumber,
                IdentityDocumentIssuedOn: null,
                IdentityDocumentExpiresOn: null,
                PhoneNumbers: [PhoneNumber],
                Email: null,
                Address: null,
                ConfirmNoDuplicate: false),
            CancellationToken.None);

        // Uniqueness of an identity document is scoped to the TENANT: the same person may
        // legitimately be a client of two institutions hosted on the platform.
        result.IsSuccess.Should().BeTrue();
        result.Value.Outcome.Should().Be(CreateClientOutcome.Created);
    }

    [Fact]
    public async Task Another_tenants_client_cannot_be_updated()
    {
        await using var seed = _factory.CreateContext();
        var foreignClient = await SeedForeignClientAsync(seed);

        await using var db = _factory.CreateContext();
        var handler = new UpdateClientHandler(
            db,
            TestDoubles.CurrentUser(TenantA, ActorId),
            TestDoubles.AgencyScope(),
            TestDoubles.Encryptor());

        var result = await handler.Handle(
            new UpdateClientCommand(
                ClientId: foreignClient.Id,
                ExpectedVersion: foreignClient.Version,
                Profession: "Modifié depuis un autre tenant",
                Employer: null,
                MaritalStatus: null,
                DeclaredIncome: null,
                DeclaredIncomeCurrency: null,
                PreferredLanguage: null),
            CancellationToken.None);

        result.Error.Should().Be(CustomerErrors.ClientNotFound);

        // And the foreign row is untouched — read back with the filters bypassed.
        await using var read = _factory.CreateContext();
        var stored = await read.Clients
            .IgnoreQueryFilters()
            .SingleAsync(c => c.Id == foreignClient.Id);

        stored.Profession.Should().NotBe("Modifié depuis un autre tenant");
    }

    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Inserts a client owned by <see cref="TenantB"/> through a <see cref="TenantA"/>
    /// context. Query filters guard reads, not writes, so the row lands for real.
    /// </summary>
    private static async Task<Client> SeedForeignClientAsync(CustomersDbContext db)
    {
        var client = TestClientFactory.Individual(
            TenantB, AgencyId,
            first: "Awa", last: "Traoré",
            clientNumber: "BKE-2026-000001",
            agencyCode: "BKE",
            identityDocumentNumber: DocumentNumber);

        client.AddContactPoint(
            ContactPointType.Phone,
            TestDoubles.Encryptor().Encrypt(PhoneNumber)!,
            TestDoubles.Indexer().Compute(BlindIndexPurpose.Phone, PhoneNumber),
            label: null,
            isPrimary: true,
            validFrom: DateTimeOffset.UtcNow.AddYears(-1),
            actor: ActorId);

        return await TestClientFactory.SeedAsync(db, client);
    }

    private static SearchClientsQuery SearchAll()
        => new(
            ClientNumber: null,
            Phone: null,
            IdentityDocumentNumber: null,
            Name: null,
            Status: null,
            AgencyId: null,
            AdvisorUserId: null,
            Type: null,
            SegmentCode: null,
            Page: 1,
            PageSize: 20);

    private static SearchClientsHandler BuildSearchHandler(CustomersDbContext db)
        => new(
            db,
            TestDoubles.CurrentUser(TenantA, ActorId),
            TestDoubles.AgencyScope(),
            TestDoubles.Encryptor(),
            TestDoubles.Indexer());
}
