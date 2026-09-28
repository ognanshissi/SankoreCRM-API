namespace Sankore.Modules.Customers.Tests.Features.Clients;

using FluentAssertions;
using Sankore.Modules.Customers.Domain;
using Sankore.Modules.Customers.Features.Clients.GetClient;
using Sankore.Modules.Customers.Infrastructure;
using Sankore.Modules.Customers.Tests.TestSupport;
using Sankore.Shared.Infrastructure.Crypto;
using Xunit;

public sealed class GetClientHandlerTests : IDisposable
{
    private static readonly Guid TenantId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid AgencyId = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid OtherAgencyId = Guid.Parse("44444444-4444-4444-4444-444444444444");
    private static readonly Guid ActorId = Guid.Parse("33333333-3333-3333-3333-333333333333");

    private const string DocumentNumber = "CI0012345642";
    private const string PhoneNumber = "+22507080918";
    private const string Email = "awa@example.com";

    private readonly TestCustomersDbContextFactory _factory = new(TenantId);

    public void Dispose() => _factory.Dispose();

    [Fact]
    public async Task Returns_every_protected_field_masked_and_never_in_clear_text()
    {
        await using var seed = _factory.CreateContext();
        var client = TestClientFactory.Individual(
            TenantId, AgencyId, first: "Awa", last: "Traoré",
            dateOfBirth: new DateOnly(1987, 4, 2),
            identityDocumentNumber: DocumentNumber);

        AddContact(client, ContactPointType.Phone, PhoneNumber);
        AddContact(client, ContactPointType.Email, Email);
        await TestClientFactory.SeedAsync(seed, client);

        await using var db = _factory.CreateContext();
        var handler = BuildHandler(db);

        var result = await handler.Handle(new GetClientQuery(client.Id), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        var dto = result.Value;

        // Names are not protected data — they must stay readable, they are the record's label.
        dto.FirstName.Should().Be("Awa");
        dto.LastName.Should().Be("Traoré");
        dto.Status.Should().Be(nameof(ClientStatus.PendingKyc));
        dto.AgencyCode.Should().Be("ABJ");

        // Protected fields: masked, and provably not the clear value.
        dto.IdentityDocumentNumberMasked.Should().NotBeNullOrWhiteSpace();
        dto.IdentityDocumentNumberMasked.Should().NotBe(DocumentNumber);
        dto.IdentityDocumentNumberMasked.Should().Contain("•");

        dto.DateOfBirthMasked.Should().Be("••/••/1987");

        dto.ContactPoints.Should().HaveCount(2);
        foreach (var contactPoint in dto.ContactPoints)
        {
            contactPoint.ValueMasked.Should().NotBeNullOrWhiteSpace();
            contactPoint.ValueMasked.Should().Contain("•");
        }

        dto.ContactPoints.Single(cp => cp.Type == nameof(ContactPointType.Phone))
            .ValueMasked.Should().NotBe(PhoneNumber);
        dto.ContactPoints.Single(cp => cp.Type == nameof(ContactPointType.Email))
            .ValueMasked.Should().NotBe(Email);

        dto.StatusHistory.Should().ContainSingle()
            .Which.NewStatus.Should().Be(nameof(ClientStatus.PendingKyc));
    }

    [Fact]
    public async Task Returns_only_the_active_contact_points()
    {
        await using var seed = _factory.CreateContext();
        var client = TestClientFactory.Individual(TenantId, AgencyId);
        AddContact(client, ContactPointType.Phone, PhoneNumber);
        var email = AddContact(client, ContactPointType.Email, Email);

        // The e-mail, not the phone: the aggregate refuses to close the last active phone
        // (LAST_PHONE_REQUIRED), so closing it here would prove nothing about the DTO.
        client.CloseContactPoint(email.Id, ActorId, DateTimeOffset.UtcNow)
            .IsSuccess.Should().BeTrue();

        await TestClientFactory.SeedAsync(seed, client);

        await using var db = _factory.CreateContext();
        var handler = BuildHandler(db);

        var result = await handler.Handle(new GetClientQuery(client.Id), CancellationToken.None);

        result.Value.ContactPoints.Should().ContainSingle()
            .Which.Type.Should().Be(nameof(ContactPointType.Phone));
    }

    [Fact]
    public async Task Keeps_only_the_five_most_recent_status_transitions()
    {
        await using var seed = _factory.CreateContext();
        var client = TestClientFactory.Individual(TenantId, AgencyId);

        // PendingKyc (creation) + 6 hand-written transitions = 7 rows for 5 slots.
        for (var i = 0; i < 6; i++)
        {
            client.RecordStatusTransition(
                ClientStatus.PendingKyc, ClientStatus.Active, $"transition {i}", ActorId);
        }

        await TestClientFactory.SeedAsync(seed, client);

        await using var db = _factory.CreateContext();
        var handler = BuildHandler(db);

        var result = await handler.Handle(new GetClientQuery(client.Id), CancellationToken.None);

        result.Value.StatusHistory.Should().HaveCount(5);
    }

    [Fact]
    public async Task Fails_with_client_not_found_for_an_unknown_client()
    {
        await using var db = _factory.CreateContext();
        var handler = BuildHandler(db);

        var result = await handler.Handle(new GetClientQuery(Guid.NewGuid()), CancellationToken.None);

        result.Error.Should().Be(CustomerErrors.ClientNotFound);
    }

    [Fact]
    public async Task Fails_with_client_not_found_rather_than_forbidden_outside_the_agency_perimeter()
    {
        await using var seed = _factory.CreateContext();
        var client = await TestClientFactory.SeedIndividualAsync(seed, TenantId, OtherAgencyId);

        await using var db = _factory.CreateContext();
        var handler = BuildHandler(db, accessibleAgencies: [AgencyId]);

        var result = await handler.Handle(new GetClientQuery(client.Id), CancellationToken.None);

        // Never AGENCY_OUT_OF_SCOPE here: the caller must not be able to probe for the
        // existence of clients held by other branches.
        result.Error.Should().Be(CustomerErrors.ClientNotFound);
    }

    [Fact]
    public async Task Points_a_merged_client_at_its_surviving_record()
    {
        await using var seed = _factory.CreateContext();
        var survivor = await TestClientFactory.SeedIndividualAsync(
            seed, TenantId, AgencyId, first: "Awa", last: "Traoré", clientNumber: "ABJ-2026-000100");

        var absorbed = TestClientFactory.Individual(
            TenantId, AgencyId, first: "Awa", last: "Traore", clientNumber: "ABJ-2026-000101");
        absorbed.MarkMerged(survivor.Id, ActorId);
        await TestClientFactory.SeedAsync(seed, absorbed);

        await using var db = _factory.CreateContext();
        var handler = BuildHandler(db);

        var result = await handler.Handle(new GetClientQuery(absorbed.Id), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Status.Should().Be(nameof(ClientStatus.Merged));
        result.Value.MergedInto.Should().NotBeNull();
        result.Value.MergedInto!.ClientId.Should().Be(survivor.Id);
        result.Value.MergedInto.ClientNumber.Should().Be("ABJ-2026-000100");
    }

    [Fact]
    public async Task Leaves_merged_into_null_for_a_live_client()
    {
        await using var seed = _factory.CreateContext();
        var client = await TestClientFactory.SeedIndividualAsync(seed, TenantId, AgencyId);

        await using var db = _factory.CreateContext();
        var handler = BuildHandler(db);

        var result = await handler.Handle(new GetClientQuery(client.Id), CancellationToken.None);

        result.Value.MergedInto.Should().BeNull();
    }

    // ─────────────────────────────────────────────────────────────────────────

    private static ClientContactPoint AddContact(Client client, ContactPointType type, string value)
        => client.AddContactPoint(
            type,
            TestDoubles.Encryptor().Encrypt(value)!,
            TestDoubles.Indexer().Compute(
                type == ContactPointType.Phone ? BlindIndexPurpose.Phone
                : type == ContactPointType.Email ? BlindIndexPurpose.Email
                : BlindIndexPurpose.PostalAddress,
                value),
            label: null,
            isPrimary: true,
            validFrom: DateTimeOffset.UtcNow.AddMonths(-3),
            actor: ActorId);

    private static GetClientHandler BuildHandler(
        CustomersDbContext db,
        params Guid[] accessibleAgencies)
        => new(
            db,
            TestDoubles.CurrentUser(TenantId, ActorId),
            TestDoubles.AgencyScope(accessibleAgencies),
            TestDoubles.Encryptor());
}
