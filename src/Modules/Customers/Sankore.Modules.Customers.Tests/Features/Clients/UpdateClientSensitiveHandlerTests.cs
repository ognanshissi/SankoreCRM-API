namespace Sankore.Modules.Customers.Tests.Features.Clients;

using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Customers.Domain;
using Sankore.Modules.Customers.Features.Clients.Shared;
using Sankore.Modules.Customers.Features.Clients.UpdateClientSensitive;
using Sankore.Modules.Customers.Infrastructure;
using Sankore.Modules.Customers.PublicApi.Events;
using Sankore.Modules.Customers.Tests.TestSupport;
using Sankore.Shared.Infrastructure.Crypto;
using Sankore.Shared.Infrastructure.Outbox;
using Sankore.Shared.Kernel;
using Xunit;

public sealed class UpdateClientSensitiveHandlerTests : IDisposable
{
    private static readonly Guid TenantId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid AgencyId = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid ActorId = Guid.Parse("33333333-3333-3333-3333-333333333333");

    private const string ValidReason = "Carte nationale d'identité renouvelée en agence";
    private const string NewDocumentNumber = "CI9988776655";

    private readonly TestCustomersDbContextFactory _factory = new(TenantId);

    public void Dispose() => _factory.Dispose();

    [Fact]
    public async Task Fails_with_reason_required_when_the_motive_is_missing()
    {
        await using var seed = _factory.CreateContext();
        var client = await TestClientFactory.SeedIndividualAsync(seed, TenantId, AgencyId);

        await using var db = _factory.CreateContext();
        var handler = BuildHandler(db);

        var result = await handler.Handle(
            Command(client.Id, client.Version, reason: "   ") with { LastName = "Koné" },
            CancellationToken.None);

        result.Error.Should().Be(CustomerErrors.ReasonRequired);
    }

    [Fact]
    public async Task Fails_with_reason_required_when_the_motive_is_too_short_to_justify_anything()
    {
        await using var seed = _factory.CreateContext();
        var client = await TestClientFactory.SeedIndividualAsync(seed, TenantId, AgencyId);

        await using var db = _factory.CreateContext();
        var handler = BuildHandler(db);

        var result = await handler.Handle(
            Command(client.Id, client.Version, reason: "maj") with { LastName = "Koné" },
            CancellationToken.None);

        result.Error.Should().Be(CustomerErrors.ReasonRequired);
    }

    [Fact]
    public async Task Publishes_an_event_carrying_field_names_and_no_value_at_all()
    {
        await using var seed = _factory.CreateContext();
        var client = await TestClientFactory.SeedIndividualAsync(
            seed, TenantId, AgencyId, first: "Awa", last: "Ouattara",
            identityDocumentNumber: "CI0011223344");

        await using var db = _factory.CreateContext();
        var handler = BuildHandler(db);

        var result = await handler.Handle(
            Command(client.Id, client.Version) with
            {
                LastName = "Koné",
                IdentityDocumentNumber = NewDocumentNumber,
            },
            CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().Contain(nameof(Client.LastName));
        result.Value.Should().Contain(nameof(SensitiveField.IdentityDocumentNumber));

        await using var read = _factory.CreateContext();
        var outbox = await read.Set<OutboxMessage>().SingleAsync();

        outbox.EventType.Should().Contain(nameof(ClientSensitiveDataChangedEvent));
        outbox.PayloadJson.Should().Contain(nameof(Client.LastName));

        // The regulated values must not travel, neither clear nor encrypted.
        outbox.PayloadJson.Should().NotContain(NewDocumentNumber);
        outbox.PayloadJson.Should().NotContain("Koné");
        outbox.PayloadJson.Should().NotContain("enc:");
    }

    [Fact]
    public async Task Recomputes_the_display_name_and_the_phonetic_keys_when_a_name_changes()
    {
        await using var seed = _factory.CreateContext();
        var client = await TestClientFactory.SeedIndividualAsync(
            seed, TenantId, AgencyId, first: "Awa", last: "Ouattara");
        var previousKey = client.PhoneticKeyPrimary;

        await using var db = _factory.CreateContext();
        var handler = BuildHandler(db);

        await handler.Handle(
            Command(client.Id, client.Version) with { LastName = "Koné" },
            CancellationToken.None);

        await using var read = _factory.CreateContext();
        var stored = await read.Clients.SingleAsync(c => c.Id == client.Id);

        stored.LastName.Should().Be("Koné");
        stored.DisplayName.Should().Contain("Koné");
        // The nightly duplicate detector blocks on this key: a stale key would make the
        // corrected record unmatchable.
        stored.PhoneticKeyPrimary.Should().NotBe(previousKey);
        // SearchKey is accent-free and surname first, so the prefix search still finds it.
        stored.SearchKey.Should().StartWith("KONE");
    }

    [Fact]
    public async Task Fails_with_duplicate_identity_document_when_the_number_belongs_to_another_client()
    {
        await using var seed = _factory.CreateContext();
        await TestClientFactory.SeedIndividualAsync(
            seed, TenantId, AgencyId,
            first: "Ibrahim", last: "Kone",
            clientNumber: "ABJ-2026-000009",
            identityDocumentNumber: NewDocumentNumber);

        var client = await TestClientFactory.SeedIndividualAsync(
            seed, TenantId, AgencyId, clientNumber: "ABJ-2026-000010");

        await using var db = _factory.CreateContext();
        var handler = BuildHandler(db);

        var result = await handler.Handle(
            Command(client.Id, client.Version) with { IdentityDocumentNumber = NewDocumentNumber },
            CancellationToken.None);

        result.Error.Should().Be(CustomerErrors.DuplicateIdentityDocument);
    }

    [Fact]
    public async Task Accepts_resubmitting_the_clients_own_document_number()
    {
        const string ownNumber = "CI5544332211";

        await using var seed = _factory.CreateContext();
        var client = await TestClientFactory.SeedIndividualAsync(
            seed, TenantId, AgencyId, identityDocumentNumber: ownNumber);

        await using var db = _factory.CreateContext();
        var handler = BuildHandler(db);

        // The probe excludes the client itself, otherwise a no-op edit (or a change of the
        // issue date alone) would collide with its own row.
        var result = await handler.Handle(
            Command(client.Id, client.Version) with
            {
                IdentityDocumentNumber = ownNumber,
                IdentityDocumentIssuedOn = new DateOnly(2026, 2, 1),
            },
            CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
    }

    [Fact]
    public async Task Closes_the_previous_address_instead_of_overwriting_it()
    {
        await using var seed = _factory.CreateContext();
        var client = TestClientFactory.Individual(TenantId, AgencyId);

        const string oldAddress = "05 rue Ancienne, Abidjan, CI";
        client.AddContactPoint(
            ContactPointType.Address,
            TestDoubles.Encryptor().Encrypt(oldAddress)!,
            TestDoubles.Indexer().Compute(BlindIndexPurpose.PostalAddress, oldAddress),
            label: "Domicile",
            isPrimary: true,
            validFrom: DateTimeOffset.UtcNow.AddYears(-2),
            actor: ActorId);

        await TestClientFactory.SeedAsync(seed, client);

        await using var db = _factory.CreateContext();
        var handler = BuildHandler(db);

        var result = await handler.Handle(
            Command(client.Id, client.Version) with
            {
                Address = new PostalAddressInput("12 rue Neuve", "Bouaké", "Vallée du Bandama", "CI", "02"),
            },
            CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().Contain(nameof(SensitiveField.PostalAddress));

        await using var read = _factory.CreateContext();
        var addresses = await read.ClientContactPoints
            .Where(cp => cp.ClientId == client.Id && cp.Type == ContactPointType.Address)
            .ToListAsync();

        // Historized, never deleted: the previous address stays auditable.
        addresses.Should().HaveCount(2);
        addresses.Count(a => a.ValidTo == null).Should().Be(1);
        addresses.Should().ContainSingle(a => a.ValidTo != null);
    }

    [Fact]
    public async Task Does_not_touch_the_address_when_the_same_value_is_resubmitted()
    {
        const string address = "12 rue Neuve, 02, Bouaké, Vallée du Bandama, CI";

        await using var seed = _factory.CreateContext();
        var client = TestClientFactory.Individual(TenantId, AgencyId);
        client.AddContactPoint(
            ContactPointType.Address,
            TestDoubles.Encryptor().Encrypt(address)!,
            TestDoubles.Indexer().Compute(BlindIndexPurpose.PostalAddress, address),
            label: null,
            isPrimary: true,
            validFrom: DateTimeOffset.UtcNow.AddYears(-1),
            actor: ActorId);
        await TestClientFactory.SeedAsync(seed, client);

        await using var db = _factory.CreateContext();
        var handler = BuildHandler(db);

        var result = await handler.Handle(
            Command(client.Id, client.Version) with
            {
                Address = new PostalAddressInput("12 rue Neuve", "Bouaké", "Vallée du Bandama", "CI", "02"),
            },
            CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        // Compared on the blind index — comparing ciphertexts would always differ, since
        // AES-GCM uses a fresh nonce per call.
        result.Value.Should().NotContain(nameof(SensitiveField.PostalAddress));

        await using var read = _factory.CreateContext();
        (await read.ClientContactPoints.CountAsync(cp =>
            cp.ClientId == client.Id && cp.Type == ContactPointType.Address)).Should().Be(1);
    }

    [Fact]
    public async Task Publishes_nothing_when_no_field_actually_changed()
    {
        await using var seed = _factory.CreateContext();
        var client = await TestClientFactory.SeedIndividualAsync(
            seed, TenantId, AgencyId, first: "Awa", last: "Ouattara");

        await using var db = _factory.CreateContext();
        var handler = BuildHandler(db);

        var result = await handler.Handle(
            Command(client.Id, client.Version) with { LastName = "Ouattara" },
            CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().BeEmpty();

        await using var read = _factory.CreateContext();
        (await read.Set<OutboxMessage>().CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task Fails_with_concurrency_conflict_on_a_stale_expected_version()
    {
        await using var seed = _factory.CreateContext();
        var client = await TestClientFactory.SeedIndividualAsync(seed, TenantId, AgencyId);

        await using var db = _factory.CreateContext();
        var handler = BuildHandler(db);

        var result = await handler.Handle(
            Command(client.Id, client.Version + 3) with { LastName = "Koné" },
            CancellationToken.None);

        result.Error.Should().Be(CustomerErrors.ConcurrencyConflict);
    }

    // ─────────────────────────────────────────────────────────────────────────

    private static UpdateClientSensitiveCommand Command(
        Guid clientId, uint expectedVersion, string reason = ValidReason)
        => new(
            ClientId: clientId,
            ExpectedVersion: expectedVersion,
            Reason: reason,
            FirstName: null,
            LastName: null,
            MaidenName: null,
            IdentityDocumentType: null,
            IdentityDocumentNumber: null,
            IdentityDocumentIssuedOn: null,
            IdentityDocumentExpiresOn: null,
            Address: null);

    private static UpdateClientSensitiveHandler BuildHandler(CustomersDbContext db)
        => new(
            db,
            TestDoubles.CurrentUser(TenantId, ActorId),
            TestDoubles.AgencyScope(),
            new DuplicateProbe(db),
            TestDoubles.Encryptor(),
            TestDoubles.Indexer(),
            ClientsTestHarness.PhoneticKeys(),
            new OutboxEventPublisher<CustomersDbContext>(db));
}
