namespace Sankore.Modules.Customers.Tests.Features.Clients;

using FluentAssertions;
using Sankore.Modules.Customers.Domain;
using Sankore.Modules.Customers.Features.Clients.SearchClients;
using Sankore.Modules.Customers.Infrastructure;
using Sankore.Modules.Customers.Tests.TestSupport;
using Sankore.Shared.Infrastructure.Crypto;
using Xunit;

public sealed class SearchClientsHandlerTests : IDisposable
{
    private static readonly Guid TenantId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid AgencyId = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid OtherAgencyId = Guid.Parse("44444444-4444-4444-4444-444444444444");
    private static readonly Guid ActorId = Guid.Parse("33333333-3333-3333-3333-333333333333");
    private static readonly Guid AdvisorId = Guid.Parse("55555555-5555-5555-5555-555555555555");

    private const string DocumentNumber = "CI0012345642";
    private const string PhoneNumber = "+225 07 08 09 18";

    private readonly TestCustomersDbContextFactory _factory = new(TenantId);

    public void Dispose() => _factory.Dispose();

    [Fact]
    public async Task Finds_a_client_by_its_exact_client_number()
    {
        await using var seed = _factory.CreateContext();
        await TestClientFactory.SeedIndividualAsync(
            seed, TenantId, AgencyId, clientNumber: "ABJ-2026-000001");
        await TestClientFactory.SeedIndividualAsync(
            seed, TenantId, AgencyId, first: "Ibrahim", last: "Kone", clientNumber: "ABJ-2026-000002");

        await using var db = _factory.CreateContext();
        var handler = BuildHandler(db, out _);

        var result = await handler.Handle(
            Query() with { ClientNumber = "abj-2026-000002" }, CancellationToken.None);

        // Client numbers are minted upper-case, so a lower-case entry is upper-cased before
        // the comparison rather than making the column non-indexable.
        result.Value.Items.Should().ContainSingle()
            .Which.ClientNumber.Should().Be("ABJ-2026-000002");
    }

    [Fact]
    public async Task Finds_a_client_by_phone_through_the_blind_index_whatever_the_spelling()
    {
        await using var seed = _factory.CreateContext();
        var client = TestClientFactory.Individual(TenantId, AgencyId, clientNumber: "ABJ-2026-000001");
        AddPhone(client, PhoneNumber);
        await TestClientFactory.SeedAsync(seed, client);

        await TestClientFactory.SeedIndividualAsync(
            seed, TenantId, AgencyId, first: "Ibrahim", last: "Kone", clientNumber: "ABJ-2026-000002");

        await using var db = _factory.CreateContext();
        var handler = BuildHandler(db, out _);

        // Typed with the "00" international prefix and different spacing: the normalizer
        // folds it onto the same blind index as the stored "+225 07 08 09 18".
        var result = await handler.Handle(
            Query() with { Phone = "00225-0708.0918" }, CancellationToken.None);

        result.Value.Items.Should().ContainSingle().Which.Id.Should().Be(client.Id);
    }

    [Fact]
    public async Task Ignores_a_closed_phone_when_searching_by_number()
    {
        await using var seed = _factory.CreateContext();
        var client = TestClientFactory.Individual(TenantId, AgencyId);
        AddPhone(client, "+22509090909");
        var old = AddPhone(client, PhoneNumber);
        client.CloseContactPoint(old.Id, ActorId, DateTimeOffset.UtcNow).IsSuccess.Should().BeTrue();
        await TestClientFactory.SeedAsync(seed, client);

        await using var db = _factory.CreateContext();
        var handler = BuildHandler(db, out _);

        var result = await handler.Handle(Query() with { Phone = PhoneNumber }, CancellationToken.None);

        // A number the client no longer uses must not surface them.
        result.Value.Items.Should().BeEmpty();
    }

    [Fact]
    public async Task Finds_a_client_by_identity_document_number_through_the_blind_index()
    {
        await using var seed = _factory.CreateContext();
        var client = await TestClientFactory.SeedIndividualAsync(
            seed, TenantId, AgencyId, identityDocumentNumber: DocumentNumber);
        await TestClientFactory.SeedIndividualAsync(
            seed, TenantId, AgencyId, first: "Ibrahim", last: "Kone", clientNumber: "ABJ-2026-000002");

        await using var db = _factory.CreateContext();
        var handler = BuildHandler(db, out _);

        // Spaces and lower case are normalized away before hashing.
        var result = await handler.Handle(
            Query() with { IdentityDocumentNumber = "ci 0012 3456 42" }, CancellationToken.None);

        result.Value.Items.Should().ContainSingle().Which.Id.Should().Be(client.Id);
    }

    [Fact]
    public async Task Finds_a_client_by_surname_prefix_ignoring_accents_and_case()
    {
        await using var seed = _factory.CreateContext();
        var client = await TestClientFactory.SeedIndividualAsync(
            seed, TenantId, AgencyId, first: "Awa", last: "Traoré");
        await TestClientFactory.SeedIndividualAsync(
            seed, TenantId, AgencyId, first: "Ibrahim", last: "Kone", clientNumber: "ABJ-2026-000002");

        await using var db = _factory.CreateContext();
        var handler = BuildHandler(db, out _);

        var result = await handler.Handle(Query() with { Name = "trao" }, CancellationToken.None);

        result.Value.Items.Should().ContainSingle().Which.Id.Should().Be(client.Id);
    }

    [Fact]
    public async Task Finds_a_client_by_an_accented_search_term_too()
    {
        await using var seed = _factory.CreateContext();
        var client = await TestClientFactory.SeedIndividualAsync(
            seed, TenantId, AgencyId, first: "Awa", last: "Traore");

        await using var db = _factory.CreateContext();
        var handler = BuildHandler(db, out _);

        // Both sides go through SearchKeyBuilder.NormalizeTerm, so the accent the agent
        // typed does not make an unaccented record unreachable (nor the reverse).
        var result = await handler.Handle(Query() with { Name = "Traoré" }, CancellationToken.None);

        result.Value.Items.Should().ContainSingle().Which.Id.Should().Be(client.Id);
    }

    [Fact]
    public async Task Finds_a_client_by_given_name_as_well_as_by_surname()
    {
        await using var seed = _factory.CreateContext();
        var client = await TestClientFactory.SeedIndividualAsync(
            seed, TenantId, AgencyId, first: "Fatoumata", last: "Traoré");

        await using var db = _factory.CreateContext();
        var handler = BuildHandler(db, out _);

        // SearchKey is "TRAORE FATOUMATA": the given name is matched by the " " + term
        // containment, not by the prefix.
        var result = await handler.Handle(Query() with { Name = "fatou" }, CancellationToken.None);

        result.Value.Items.Should().ContainSingle().Which.Id.Should().Be(client.Id);
    }

    [Fact]
    public async Task Ignores_a_name_shorter_than_three_characters()
    {
        await using var seed = _factory.CreateContext();
        await TestClientFactory.SeedIndividualAsync(seed, TenantId, AgencyId, last: "Traoré");
        await TestClientFactory.SeedIndividualAsync(
            seed, TenantId, AgencyId, first: "Ibrahim", last: "Kone", clientNumber: "ABJ-2026-000002");

        await using var db = _factory.CreateContext();
        var handler = BuildHandler(db, out _);

        var result = await handler.Handle(Query() with { Name = "tr" }, CancellationToken.None);

        // Two characters match too much to be useful, so the filter is dropped rather than
        // returning a misleading subset.
        result.Value.TotalCount.Should().Be(2);
    }

    [Fact]
    public async Task Combines_every_filter_cumulatively()
    {
        await using var seed = _factory.CreateContext();
        var wanted = TestClientFactory.Individual(
            TenantId, AgencyId, first: "Awa", last: "Traoré",
            clientNumber: "ABJ-2026-000001", advisorUserId: AdvisorId);
        wanted.SetSegment("PREMIUM", DateTimeOffset.UtcNow);
        await TestClientFactory.SeedAsync(seed, wanted);

        // Same surname, different advisor and segment.
        var decoy = TestClientFactory.Individual(
            TenantId, AgencyId, first: "Ali", last: "Traoré", clientNumber: "ABJ-2026-000002");
        decoy.SetSegment("STANDARD", DateTimeOffset.UtcNow);
        await TestClientFactory.SeedAsync(seed, decoy);

        await using var db = _factory.CreateContext();
        var handler = BuildHandler(db, out _);

        var result = await handler.Handle(
            Query() with
            {
                Name = "traore",
                Status = ClientStatus.PendingKyc,
                Type = ClientType.Individual,
                AgencyId = AgencyId,
                AdvisorUserId = AdvisorId,
                SegmentCode = "PREMIUM",
            },
            CancellationToken.None);

        result.Value.Items.Should().ContainSingle().Which.Id.Should().Be(wanted.Id);
    }

    [Fact]
    public async Task Paginates_and_orders_by_display_name()
    {
        await using var seed = _factory.CreateContext();
        foreach (var (last, number) in new[] { ("Coulibaly", "1"), ("Bamba", "2"), ("Aka", "3"), ("Diallo", "4") })
        {
            await TestClientFactory.SeedIndividualAsync(
                seed, TenantId, AgencyId, last: last, clientNumber: $"ABJ-2026-00000{number}");
        }

        await using var db = _factory.CreateContext();
        var handler = BuildHandler(db, out _);

        var firstPage = await handler.Handle(
            Query() with { Page = 1, PageSize = 2 }, CancellationToken.None);

        firstPage.Value.TotalCount.Should().Be(4);
        firstPage.Value.Items.Should().HaveCount(2);
        firstPage.Value.Items[0].DisplayName.Should().Contain("Aka");
        firstPage.Value.Items[1].DisplayName.Should().Contain("Bamba");

        var secondPage = await handler.Handle(
            Query() with { Page = 2, PageSize = 2 }, CancellationToken.None);

        secondPage.Value.Items.Should().HaveCount(2);
        secondPage.Value.Items[0].DisplayName.Should().Contain("Coulibaly");
    }

    [Fact]
    public async Task Clamps_a_non_positive_page_to_the_first_one()
    {
        await using var seed = _factory.CreateContext();
        await TestClientFactory.SeedIndividualAsync(seed, TenantId, AgencyId);

        await using var db = _factory.CreateContext();
        var handler = BuildHandler(db, out _);

        // A negative page would otherwise produce Skip(-n), which the provider rejects.
        var result = await handler.Handle(
            Query() with { Page = 0, PageSize = 20 }, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Page.Should().Be(1);
        result.Value.Items.Should().ContainSingle();
    }

    [Fact]
    public async Task Restricts_the_results_to_the_callers_agency_perimeter()
    {
        await using var seed = _factory.CreateContext();
        var mine = await TestClientFactory.SeedIndividualAsync(
            seed, TenantId, AgencyId, clientNumber: "ABJ-2026-000001");
        await TestClientFactory.SeedIndividualAsync(
            seed, TenantId, OtherAgencyId, first: "Ibrahim", last: "Kone",
            clientNumber: "BKE-2026-000001", agencyCode: "BKE");

        await using var db = _factory.CreateContext();
        var handler = BuildHandler(db, out _, accessibleAgencies: [AgencyId]);

        var result = await handler.Handle(Query(), CancellationToken.None);

        result.Value.TotalCount.Should().Be(1);
        result.Value.Items.Should().ContainSingle().Which.Id.Should().Be(mine.Id);
    }

    [Fact]
    public async Task Returns_every_agency_for_an_unrestricted_caller()
    {
        await using var seed = _factory.CreateContext();
        await TestClientFactory.SeedIndividualAsync(
            seed, TenantId, AgencyId, clientNumber: "ABJ-2026-000001");
        await TestClientFactory.SeedIndividualAsync(
            seed, TenantId, OtherAgencyId, first: "Ibrahim", last: "Kone",
            clientNumber: "BKE-2026-000001", agencyCode: "BKE");

        await using var db = _factory.CreateContext();
        // No accessible id: the provider answers null, which means UNRESTRICTED.
        var handler = BuildHandler(db, out _);

        var result = await handler.Handle(Query(), CancellationToken.None);

        result.Value.TotalCount.Should().Be(2);
    }

    [Fact]
    public async Task Masks_the_primary_phone_of_each_row()
    {
        await using var seed = _factory.CreateContext();
        var client = TestClientFactory.Individual(TenantId, AgencyId);
        AddPhone(client, PhoneNumber);
        await TestClientFactory.SeedAsync(seed, client);

        await using var db = _factory.CreateContext();
        var handler = BuildHandler(db, out _);

        var result = await handler.Handle(Query(), CancellationToken.None);

        var item = result.Value.Items.Should().ContainSingle().Subject;
        item.PrimaryPhoneMasked.Should().NotBeNullOrWhiteSpace();
        item.PrimaryPhoneMasked.Should().Contain("•");
        item.PrimaryPhoneMasked.Should().NotBe(PhoneNumber);
    }

    [Fact]
    public async Task Decrypts_at_most_one_value_per_returned_row_and_never_to_filter()
    {
        await using var seed = _factory.CreateContext();
        for (var i = 1; i <= 6; i++)
        {
            var client = TestClientFactory.Individual(
                TenantId, AgencyId, last: $"Client{i}", clientNumber: $"ABJ-2026-00000{i}");
            AddPhone(client, $"+2250708091{i}");
            await TestClientFactory.SeedAsync(seed, client);
        }

        await using var db = _factory.CreateContext();
        var handler = BuildHandler(db, out var encryptor);

        var result = await handler.Handle(
            Query() with { Page = 1, PageSize = 2 }, CancellationToken.None);

        result.Value.TotalCount.Should().Be(6);
        result.Value.Items.Should().HaveCount(2);

        // Non-regression guard: the cost of masking is bounded by the PAGE, not by the
        // table. A regression that materialised the whole result set (a premature ToList,
        // or in-memory filtering) would show up here as 6 decrypt calls instead of 2.
        encryptor.DecryptCalls.Should().BeLessThanOrEqualTo(2);
    }

    [Fact]
    public async Task Filters_by_phone_without_decrypting_anything_to_match()
    {
        await using var seed = _factory.CreateContext();
        for (var i = 1; i <= 5; i++)
        {
            var client = TestClientFactory.Individual(
                TenantId, AgencyId, last: $"Client{i}", clientNumber: $"ABJ-2026-00000{i}");
            AddPhone(client, $"+2250708091{i}");
            await TestClientFactory.SeedAsync(seed, client);
        }

        await using var db = _factory.CreateContext();
        var handler = BuildHandler(db, out var encryptor);

        var result = await handler.Handle(
            Query() with { Phone = "+22507080913" }, CancellationToken.None);

        result.Value.Items.Should().ContainSingle();

        // One decrypt, and it is the masking of the single returned row — the MATCHING
        // itself compared blind indexes and touched no ciphertext.
        encryptor.DecryptCalls.Should().Be(1);
    }

    // ─────────────────────────────────────────────────────────────────────────

    private static ClientContactPoint AddPhone(Client client, string phone)
        => client.AddContactPoint(
            ContactPointType.Phone,
            TestDoubles.Encryptor().Encrypt(phone)!,
            TestDoubles.Indexer().Compute(BlindIndexPurpose.Phone, phone),
            label: null,
            isPrimary: true,
            validFrom: DateTimeOffset.UtcNow.AddMonths(-1),
            actor: ActorId);

    private static SearchClientsQuery Query()
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

    private static SearchClientsHandler BuildHandler(
        CustomersDbContext db,
        out ClientsTestHarness.CountingFieldEncryptor encryptor,
        params Guid[] accessibleAgencies)
    {
        encryptor = new ClientsTestHarness.CountingFieldEncryptor(TestDoubles.Encryptor());

        return new SearchClientsHandler(
            db,
            TestDoubles.CurrentUser(TenantId, ActorId),
            TestDoubles.AgencyScope(accessibleAgencies),
            encryptor,
            TestDoubles.Indexer());
    }
}
