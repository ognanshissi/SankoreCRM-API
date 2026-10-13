namespace Sankore.Modules.Customers.Tests.Infrastructure;

using FluentAssertions;
using Sankore.Modules.Customers.Domain;
using Sankore.Modules.Customers.Infrastructure;
using Sankore.Modules.Customers.Tests.TestSupport;
using Sankore.Shared.Infrastructure.Crypto;
using Xunit;

/// <summary>
/// Covers <c>ICustomersModule.FindMatchesAsync</c> (BNF-B-22) — how M13 recognises that a captured
/// lead is a request from a client M01 already holds.
/// </summary>
public sealed class FindMatchesTests : IDisposable
{
    private readonly Guid _tenant = Guid.NewGuid();
    private readonly Guid _agency = Guid.NewGuid();
    private static readonly Guid Actor = Guid.NewGuid();

    private readonly TestCustomersDbContextFactory _factory;
    private static readonly IFieldEncryptor Encryptor = TestDoubles.Encryptor();
    private static readonly IBlindIndexer Indexer = TestDoubles.Indexer();

    public FindMatchesTests() => _factory = new TestCustomersDbContextFactory(_tenant);

    public void Dispose() => _factory.Dispose();

    private CustomersModuleFacade Facade(CustomersDbContext db)
        => new(db, new UnusedLeadConversion(), Indexer, TestHoldingSync.For(db, _tenant),
               TestDoubles.ClientEmails(db), TestDoubles.RemoteIdentity(db), TestDoubles.ReadIdentity(db));

    private sealed class UnusedLeadConversion : ILeadConversionService
    {
        public Task<Sankore.Shared.Kernel.Result<PublicApi.CreateFromLeadResult>> CreateFromLeadAsync(
            PublicApi.CreateFromLeadRequest request, CancellationToken ct)
            => throw new NotSupportedException("Matching never converts a lead.");

        public Task<Sankore.Shared.Kernel.Result<Guid>> CreateHoldingFromLeadAsync(
            PublicApi.CreateHoldingFromLeadRequest request, CancellationToken ct)
            => throw new NotSupportedException("Matching never opens a holding.");
    }

    private Client WithContacts(
        string clientNumber,
        string? phone = null,
        string? email = null,
        string? documentNumber = null,
        ClientStatus status = ClientStatus.Active,
        Guid? advisorUserId = null,
        bool closeContacts = false)
    {
        var client = TestClientFactory.Individual(
            _tenant, _agency,
            clientNumber: clientNumber,
            advisorUserId: advisorUserId,
            identityDocumentNumber: documentNumber);

        if (phone is not null)
            Add(client, ContactPointType.Phone, phone, BlindIndexPurpose.Phone, closeContacts);
        if (email is not null)
            Add(client, ContactPointType.Email, email, BlindIndexPurpose.Email, closeContacts);

        if (status != ClientStatus.Active)
            ForceStatus(client, status);

        return client;
    }

    private static void Add(
        Client client, ContactPointType type, string value, BlindIndexPurpose purpose, bool closed)
    {
        var point = client.AddContactPoint(
            type,
            Encryptor.Encrypt(value)!,
            Indexer.Compute(purpose, value),
            label: null,
            isPrimary: true,
            validFrom: DateTimeOffset.UtcNow.AddMonths(-1),
            actor: Actor);

        if (closed)
            point.GetType().GetMethod("Close", System.Reflection.BindingFlags.NonPublic
                                             | System.Reflection.BindingFlags.Instance)!
                 .Invoke(point, [DateTimeOffset.UtcNow]);
    }

    /// <summary>
    /// Statuses have no public setter — they move through the lifecycle commands. These tests only
    /// need the stored value, so it is set directly rather than by driving a whole state machine
    /// that is covered elsewhere.
    /// </summary>
    private static void ForceStatus(Client client, ClientStatus status) =>
        typeof(Client).GetProperty(nameof(Client.Status))!
            .SetValue(client, status);

    private async Task SeedAsync(params Client[] clients)
    {
        await using var db = _factory.CreateContext();
        db.Clients.AddRange(clients);
        await db.SaveChangesAsync(CancellationToken.None);
    }

    // ── Matching ──────────────────────────────────────────────────────────

    [Fact]
    public async Task Matches_on_a_phone_number()
    {
        await SeedAsync(WithContacts("CLI-0001", phone: "+2250700000001"));

        await using var db = _factory.CreateContext();
        var matches = await Facade(db).FindMatchesAsync(
            _tenant, "+2250700000001", null, null, CancellationToken.None);

        matches.Should().ContainSingle();
        matches[0].ClientNumber.Should().Be("CLI-0001");
        matches[0].MatchedOn.Should().BeEquivalentTo(["phone"]);
    }

    [Fact]
    public async Task Matches_on_an_email_whatever_its_casing()
    {
        // The normalisation is the shared one, so the stored index and the probe agree.
        await SeedAsync(WithContacts("CLI-0001", email: "aya@example.ci"));

        await using var db = _factory.CreateContext();
        var matches = await Facade(db).FindMatchesAsync(
            _tenant, null, "AYA@EXAMPLE.CI", null, CancellationToken.None);

        matches.Should().ContainSingle();
        matches[0].MatchedOn.Should().BeEquivalentTo(["email"]);
    }

    [Fact]
    public async Task Matches_on_an_identity_document()
    {
        await SeedAsync(WithContacts("CLI-0001", documentNumber: "CI12345642"));

        await using var db = _factory.CreateContext();
        var matches = await Facade(db).FindMatchesAsync(
            _tenant, null, null, "CI12345642", CancellationToken.None);

        matches.Should().ContainSingle();
        matches[0].MatchedOn.Should().BeEquivalentTo(["identityDocument"]);
    }

    [Fact]
    public async Task Reports_every_identifier_that_matched()
    {
        // The agent confirming a suggestion needs to see WHY it was suggested — and the
        // auto-attach rule reads this list.
        await SeedAsync(WithContacts(
            "CLI-0001", phone: "+2250700000001", email: "aya@example.ci",
            documentNumber: "CI12345642"));

        await using var db = _factory.CreateContext();
        var matches = await Facade(db).FindMatchesAsync(
            _tenant, "+2250700000001", "aya@example.ci", "CI12345642", CancellationToken.None);

        matches.Should().ContainSingle();
        matches[0].MatchedOn.Should().BeEquivalentTo(["phone", "email", "identityDocument"]);
    }

    [Fact]
    public async Task Returns_every_candidate_when_several_share_an_identifier()
    {
        // A family handset. Deciding between them is the agent's job, so both come back.
        await SeedAsync(
            WithContacts("CLI-0001", phone: "+2250700000001"),
            WithContacts("CLI-0002", phone: "+2250700000001"));

        await using var db = _factory.CreateContext();
        var matches = await Facade(db).FindMatchesAsync(
            _tenant, "+2250700000001", null, null, CancellationToken.None);

        matches.Should().HaveCount(2);
    }

    [Fact]
    public async Task Orders_the_most_corroborated_candidate_first()
    {
        await SeedAsync(
            WithContacts("CLI-0001", phone: "+2250700000001"),
            WithContacts("CLI-0002", phone: "+2250700000001", documentNumber: "CI12345642"));

        await using var db = _factory.CreateContext();
        var matches = await Facade(db).FindMatchesAsync(
            _tenant, "+2250700000001", null, "CI12345642", CancellationToken.None);

        matches[0].ClientNumber.Should().Be("CLI-0002");
        matches[0].MatchedOn.Should().HaveCount(2);
    }

    // ── What must NOT match ───────────────────────────────────────────────

    [Fact]
    public async Task No_identifier_returns_nothing_without_touching_the_database()
    {
        await SeedAsync(WithContacts("CLI-0001", phone: "+2250700000001"));

        await using var db = _factory.CreateContext();
        var matches = await Facade(db).FindMatchesAsync(
            _tenant, "  ", null, null, CancellationToken.None);

        matches.Should().BeEmpty();
    }

    [Theory]
    [InlineData(ClientStatus.Archived)]
    [InlineData(ClientStatus.Merged)]
    public async Task An_archived_or_merged_client_is_not_a_customer_making_a_request(ClientStatus status)
    {
        // Attaching to an archived record would revive it by the side door; to a merged one would
        // point the request at an id that no longer answers.
        await SeedAsync(WithContacts("CLI-0001", phone: "+2250700000001", status: status));

        await using var db = _factory.CreateContext();
        var matches = await Facade(db).FindMatchesAsync(
            _tenant, "+2250700000001", null, null, CancellationToken.None);

        matches.Should().BeEmpty();
    }

    [Fact]
    public async Task A_client_whose_file_is_still_pending_kyc_DOES_match()
    {
        // Refusing to recognise them is exactly how a second record for the same person is created.
        await SeedAsync(WithContacts(
            "CLI-0001", phone: "+2250700000001", status: ClientStatus.PendingKyc));

        await using var db = _factory.CreateContext();
        var matches = await Facade(db).FindMatchesAsync(
            _tenant, "+2250700000001", null, null, CancellationToken.None);

        matches.Should().ContainSingle();
    }

    [Fact]
    public async Task A_closed_contact_point_does_not_match()
    {
        // A number the client USED to have. A lead reaching it is not evidence about this client.
        await SeedAsync(WithContacts("CLI-0001", phone: "+2250700000001", closeContacts: true));

        await using var db = _factory.CreateContext();
        var matches = await Facade(db).FindMatchesAsync(
            _tenant, "+2250700000001", null, null, CancellationToken.None);

        matches.Should().BeEmpty();
    }

    [Fact]
    public async Task Another_tenants_client_never_matches()
    {
        await SeedAsync(WithContacts("CLI-0001", phone: "+2250700000001"));

        await using var db = _factory.CreateContext();
        var matches = await Facade(db).FindMatchesAsync(
            Guid.NewGuid(), "+2250700000001", null, null, CancellationToken.None);

        matches.Should().BeEmpty();
    }

    // ── What travels back ─────────────────────────────────────────────────

    [Fact]
    public async Task The_match_carries_the_advisor_so_dispatching_can_route_to_them()
    {
        var advisor = Guid.NewGuid();
        await SeedAsync(WithContacts(
            "CLI-0001", phone: "+2250700000001", advisorUserId: advisor));

        await using var db = _factory.CreateContext();
        var matches = await Facade(db).FindMatchesAsync(
            _tenant, "+2250700000001", null, null, CancellationToken.None);

        matches[0].AdvisorUserId.Should().Be(advisor);
        matches[0].AgencyId.Should().Be(_agency);
    }
}
