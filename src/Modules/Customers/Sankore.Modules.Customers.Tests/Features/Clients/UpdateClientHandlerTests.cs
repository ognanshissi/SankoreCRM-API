namespace Sankore.Modules.Customers.Tests.Features.Clients;

using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Customers.Domain;
using Sankore.Modules.Customers.Features.Clients.UpdateClient;
using Sankore.Modules.Customers.Infrastructure;
using Sankore.Modules.Customers.Tests.TestSupport;
using Sankore.Shared.Kernel;
using Xunit;

public sealed class UpdateClientHandlerTests : IDisposable
{
    private static readonly Guid TenantId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid AgencyId = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid OtherAgencyId = Guid.Parse("44444444-4444-4444-4444-444444444444");
    private static readonly Guid ActorId = Guid.Parse("33333333-3333-3333-3333-333333333333");

    private readonly TestCustomersDbContextFactory _factory = new(TenantId);

    public void Dispose() => _factory.Dispose();

    [Fact]
    public async Task Updates_the_non_sensitive_fields()
    {
        await using var seed = _factory.CreateContext();
        var client = await TestClientFactory.SeedIndividualAsync(seed, TenantId, AgencyId);

        await using var db = _factory.CreateContext();
        var handler = BuildHandler(db);

        var result = await handler.Handle(
            Command(client.Id, client.Version) with
            {
                Profession = "Restauratrice",
                Employer = "Maquis Le Baobab",
                MaritalStatus = MaritalStatus.Married,
                DeclaredIncome = 320000m,
                DeclaredIncomeCurrency = "XOF",
                PreferredLanguage = "fr",
            },
            CancellationToken.None);

        result.IsSuccess.Should().BeTrue();

        await using var read = _factory.CreateContext();
        var stored = await read.Clients.SingleAsync(c => c.Id == client.Id);

        stored.Profession.Should().Be("Restauratrice");
        stored.Employer.Should().Be("Maquis Le Baobab");
        stored.MaritalStatus.Should().Be(MaritalStatus.Married);
        stored.DeclaredIncomeCurrency.Should().Be("XOF");
        stored.UpdatedBy.Should().Be(ActorId);

        // The income is money, hence protected: never a clear column.
        stored.EncryptedDeclaredIncome.Should().NotBeNullOrWhiteSpace();
        stored.EncryptedDeclaredIncome.Should().NotBe("320000");
    }

    [Fact]
    public async Task Leaves_a_null_field_untouched()
    {
        await using var seed = _factory.CreateContext();
        var client = await TestClientFactory.SeedIndividualAsync(seed, TenantId, AgencyId);
        var originalProfession = client.Profession;

        await using var db = _factory.CreateContext();
        var handler = BuildHandler(db);

        var result = await handler.Handle(
            Command(client.Id, client.Version) with { Employer = "Nouvelle SARL" },
            CancellationToken.None);

        result.IsSuccess.Should().BeTrue();

        await using var read = _factory.CreateContext();
        var stored = await read.Clients.SingleAsync(c => c.Id == client.Id);

        stored.Profession.Should().Be(originalProfession);
        stored.Employer.Should().Be("Nouvelle SARL");
    }

    [Fact]
    public async Task Fails_with_client_not_found_for_an_unknown_client()
    {
        await using var db = _factory.CreateContext();
        var handler = BuildHandler(db);

        var result = await handler.Handle(Command(Guid.NewGuid(), 0), CancellationToken.None);

        result.Error.Should().Be(CustomerErrors.ClientNotFound);
    }

    [Fact]
    public async Task Fails_with_client_not_found_when_the_client_is_outside_the_agency_perimeter()
    {
        await using var seed = _factory.CreateContext();
        var client = await TestClientFactory.SeedIndividualAsync(seed, TenantId, OtherAgencyId);

        await using var db = _factory.CreateContext();
        // The caller only sees AgencyId, and the client lives in OtherAgencyId.
        var handler = BuildHandler(db, accessibleAgencies: [AgencyId]);

        var result = await handler.Handle(Command(client.Id, client.Version), CancellationToken.None);

        // 404, not 403: a 403 would confirm that this client exists in another branch.
        result.Error.Should().Be(CustomerErrors.ClientNotFound);
    }

    [Fact]
    public async Task Fails_with_concurrency_conflict_on_a_stale_expected_version()
    {
        await using var seed = _factory.CreateContext();
        var client = await TestClientFactory.SeedIndividualAsync(seed, TenantId, AgencyId);

        await using var db = _factory.CreateContext();
        var handler = BuildHandler(db);

        var result = await handler.Handle(
            Command(client.Id, client.Version + 7) with { Profession = "Autre" },
            CancellationToken.None);

        result.Error.Should().Be(CustomerErrors.ConcurrencyConflict);

        await using var read = _factory.CreateContext();
        (await read.Clients.SingleAsync(c => c.Id == client.Id)).Profession
            .Should().NotBe("Autre");
    }

    [Fact]
    public async Task Fails_with_client_read_only_on_an_archived_client()
    {
        await using var seed = _factory.CreateContext();
        var client = TestClientFactory.Individual(TenantId, AgencyId);
        client.Archive("Compte clôturé à la demande du client", ActorId);
        await TestClientFactory.SeedAsync(seed, client);

        await using var db = _factory.CreateContext();
        var handler = BuildHandler(db);

        var result = await handler.Handle(
            Command(client.Id, client.Version) with { Profession = "Autre" },
            CancellationToken.None);

        result.Error.Should().Be(CustomerErrors.ClientReadOnly);
    }

    [Fact]
    public async Task Fails_with_client_read_only_on_a_merged_client()
    {
        await using var seed = _factory.CreateContext();
        var client = TestClientFactory.Individual(TenantId, AgencyId);
        client.MarkMerged(Guid.NewGuid(), ActorId);
        await TestClientFactory.SeedAsync(seed, client);

        await using var db = _factory.CreateContext();
        var handler = BuildHandler(db);

        var result = await handler.Handle(
            Command(client.Id, client.Version) with { Profession = "Autre" },
            CancellationToken.None);

        result.Error.Should().Be(CustomerErrors.ClientReadOnly);
    }

    // ─────────────────────────────────────────────────────────────────────────

    private static UpdateClientCommand Command(Guid clientId, uint expectedVersion)
        => new(
            ClientId: clientId,
            ExpectedVersion: expectedVersion,
            Profession: null,
            Employer: null,
            MaritalStatus: null,
            DeclaredIncome: null,
            DeclaredIncomeCurrency: null,
            PreferredLanguage: null);

    private static UpdateClientHandler BuildHandler(
        CustomersDbContext db,
        params Guid[] accessibleAgencies)
        => new(
            db,
            TestDoubles.CurrentUser(TenantId, ActorId),
            TestDoubles.AgencyScope(accessibleAgencies),
            TestDoubles.Encryptor());
}
