namespace Sankore.Modules.Customers.Tests.Features.Compliance;

using System.Globalization;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Sankore.Modules.Customers.Domain;
using Sankore.Modules.Customers.Features.Compliance.Retention;
using Sankore.Modules.Customers.Features.Compliance.Retention.ListRetentionCandidates;
using Sankore.Modules.Customers.Infrastructure;
using Sankore.Modules.Customers.Tests.TestSupport;
using Sankore.Modules.Kyc.PublicApi;
using Sankore.Shared.Infrastructure.Crypto;
using Sankore.Shared.Kernel.Authorization;
using Xunit;

/// <summary>
/// US-M01-BE-29, proposal side: the monthly scan and the endpoint that reads what it produced.
/// </summary>
public sealed class RetentionCandidatesTests : IDisposable
{
    private readonly Guid _tenantId = Guid.NewGuid();
    private readonly Guid _agencyId = Guid.NewGuid();
    private readonly Guid _userId = Guid.NewGuid();
    private readonly TestCustomersDbContextFactory _factory;
    private readonly IFieldEncryptor _encryptor = TestDoubles.Encryptor();
    private readonly IBlindIndexer _indexer = TestDoubles.Indexer();

    /// <summary>
    /// Eleven years past the archive stamp, so a ten-year retention has genuinely elapsed.
    /// </summary>
    private readonly FixedTimeProvider _clock = new(DateTimeOffset.UtcNow.AddYears(11));

    public RetentionCandidatesTests() => _factory = new TestCustomersDbContextFactory(_tenantId);

    public void Dispose() => _factory.Dispose();

    // ── The monthly scan ────────────────────────────────────────────────────

    [Fact]
    public async Task The_scan_proposes_an_archived_client_whose_retention_has_elapsed()
    {
        var client = await SeedArchivedAsync("CLI-0001");

        await RunScanAsync(retentionCleared: true);

        await using var db = _factory.CreateContext();
        var entries = await TimelineEntriesAsync(db);

        entries.Should().HaveCount(1);
        entries[0].ClientId.Should().Be(client.Id);
        entries[0].SourceModule.Should().Be("Customers");
        entries[0].EntryType.Should().Be("RETENTION_ELIGIBLE");
        entries[0].ReferenceType.Should().Be("Client");
        entries[0].ReferenceId.Should().Be(client.Id.ToString("D"));
    }

    [Fact]
    public async Task A_client_the_KYC_module_has_not_cleared_is_never_proposed()
    {
        await SeedArchivedAsync("CLI-0002");

        await RunScanAsync(retentionCleared: false);

        await using var db = _factory.CreateContext();

        // Excluded from the queue entirely — not listed with a flag for a human to filter out.
        (await TimelineEntriesAsync(db)).Should().BeEmpty();
    }

    [Fact]
    public async Task A_client_archived_inside_the_retention_window_is_not_proposed()
    {
        await SeedArchivedAsync("CLI-0003");

        // Clock barely moved: the archive is days old, not years.
        _clock.Now = DateTimeOffset.UtcNow.AddDays(3);

        await RunScanAsync(retentionCleared: true);

        await using var db = _factory.CreateContext();
        (await TimelineEntriesAsync(db)).Should().BeEmpty();
    }

    [Fact]
    public async Task A_client_that_is_not_archived_is_not_proposed()
    {
        await using (var seed = _factory.CreateContext())
        {
            seed.Clients.Add(ComplianceTestData.Individual(
                _tenantId, _agencyId, "CLI-0004", "Yao", "Traore", _userId, _encryptor, _indexer));
            await seed.SaveChangesAsync();
        }

        await RunScanAsync(retentionCleared: true);

        await using var db = _factory.CreateContext();
        (await TimelineEntriesAsync(db)).Should().BeEmpty();
    }

    [Fact]
    public async Task An_already_anonymized_client_is_not_proposed_again()
    {
        var client = await SeedArchivedAsync("CLI-0005");

        await using (var mutate = _factory.CreateContext())
        {
            var tracked = await mutate.Clients
                .AsTracking()
                .Include(c => c.ContactPoints)
                .SingleAsync(c => c.Id == client.Id);

            tracked.Anonymize(Guid.Empty, _clock.GetUtcNow());
            await mutate.SaveChangesAsync();
        }

        await RunScanAsync(retentionCleared: true);

        await using var db = _factory.CreateContext();
        (await TimelineEntriesAsync(db)).Should().BeEmpty();
    }

    [Fact]
    public async Task Running_the_scan_twice_in_the_same_month_produces_a_single_proposal()
    {
        // Hangfire retries, and the orchestrator can fan out twice on a rolling deploy. The
        // monthly dedup key is what keeps the timeline from filling up with the same fact.
        await SeedArchivedAsync("CLI-0006");

        await RunScanAsync(retentionCleared: true);
        await RunScanAsync(retentionCleared: true);
        await RunScanAsync(retentionCleared: true);

        await using var db = _factory.CreateContext();
        (await TimelineEntriesAsync(db)).Should().HaveCount(1);
    }

    [Fact]
    public async Task The_proposal_summary_carries_no_sensitive_value()
    {
        // The timeline has no reveal audit and no encryption: anything in Summary is readable by
        // every holder of customers:read.
        await SeedArchivedAsync("CLI-0007", documentNumber: "CI0123456789", phoneNumber: "+22507112233");

        await RunScanAsync(retentionCleared: true);

        await using var db = _factory.CreateContext();
        var summary = (await TimelineEntriesAsync(db)).Single().Summary;

        summary.Should().NotContain("CI0123456789");
        summary.Should().NotContain("07112233");
        summary.Should().NotContain("22507112233");
        summary.Should().Contain("CLI-0007");
    }

    [Fact]
    public async Task The_scan_never_shortens_the_retention_below_ten_years()
    {
        // A row edited straight in SQL — or seeded by an older version — must not be able to
        // bring the term under the regulatory floor.
        await SeedArchivedAsync("CLI-0008");

        _clock.Now = DateTimeOffset.UtcNow.AddYears(5);

        await RunScanAsync(retentionCleared: true, retentionYears: 1);

        await using var db = _factory.CreateContext();
        (await TimelineEntriesAsync(db)).Should().BeEmpty("the floor of 10 years applies on read too");
    }

    [Fact]
    public async Task The_scan_ignores_the_clients_of_another_tenant()
    {
        var otherTenantId = Guid.NewGuid();

        await using (var seed = _factory.CreateContext())
        {
            var foreignClient = ComplianceTestData.ArchivedIndividual(
                otherTenantId, _agencyId, "CLI-9999", _userId, _encryptor, _indexer);

            seed.Clients.Add(foreignClient);
            await seed.SaveChangesAsync();
        }

        await RunScanAsync(retentionCleared: true);

        await using var db = _factory.CreateContext();

        var allEntries = await db.ClientTimelineEntries.IgnoreQueryFilters().ToListAsync();
        allEntries.Should().BeEmpty("the scan filters on TenantId explicitly");
    }

    // ── The listing endpoint ────────────────────────────────────────────────

    [Fact]
    public async Task The_listing_returns_the_clients_the_scan_proposed()
    {
        var client = await SeedArchivedAsync("CLI-0010");
        await RunScanAsync(retentionCleared: true);

        await using var db = _factory.CreateContext();

        var result = await NewListHandler(db)
            .Handle(new ListRetentionCandidatesQuery(), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.TotalCount.Should().Be(1);

        var candidate = result.Value.Items.Single();
        candidate.ClientId.Should().Be(client.Id);
        candidate.ClientNumber.Should().Be("CLI-0010");
        candidate.AgencyId.Should().Be(_agencyId);
        candidate.ArchivedAt.Should().NotBeNull();
        candidate.IdentifiedAt.Should().Be(_clock.GetUtcNow());
    }

    [Fact]
    public async Task The_listing_exposes_no_identity_field()
    {
        await SeedArchivedAsync("CLI-0011");
        await RunScanAsync(retentionCleared: true);

        await using var db = _factory.CreateContext();

        var candidate = (await NewListHandler(db)
            .Handle(new ListRetentionCandidatesQuery(), CancellationToken.None)).Value.Items.Single();

        // The DTO's shape is the guarantee: no display name, no masked phone, no document.
        typeof(RetentionCandidateDto).GetProperties().Select(p => p.Name)
            .Should().BeEquivalentTo([
                nameof(RetentionCandidateDto.ClientId),
                nameof(RetentionCandidateDto.ClientNumber),
                nameof(RetentionCandidateDto.ClientType),
                nameof(RetentionCandidateDto.AgencyId),
                nameof(RetentionCandidateDto.AgencyCode),
                nameof(RetentionCandidateDto.ArchivedAt),
                nameof(RetentionCandidateDto.ArchiveReason),
                nameof(RetentionCandidateDto.IdentifiedAt)
            ]);

        candidate.ClientType.Should().Be(nameof(ClientType.Individual));
    }

    [Fact]
    public async Task The_listing_hides_a_candidate_outside_the_callers_agency_perimeter()
    {
        await SeedArchivedAsync("CLI-0012");
        await RunScanAsync(retentionCleared: true);

        await using var db = _factory.CreateContext();

        // A perimeter that contains some other agency, never this client's.
        var elsewhere = TestDoubles.AgencyScope(Guid.NewGuid());

        var result = await NewListHandler(db, elsewhere)
            .Handle(new ListRetentionCandidatesQuery(), CancellationToken.None);

        result.Value.TotalCount.Should().Be(0);
        result.Value.Items.Should().BeEmpty();
    }

    [Fact]
    public async Task The_listing_pages_and_clamps_an_out_of_range_page()
    {
        for (var i = 0; i < 5; i++)
            await SeedArchivedAsync($"CLI-100{i}");

        await RunScanAsync(retentionCleared: true);

        await using var db = _factory.CreateContext();
        var handler = NewListHandler(db);

        var firstPage = await handler.Handle(new ListRetentionCandidatesQuery(Page: 1, PageSize: 2),
            CancellationToken.None);

        firstPage.Value.TotalCount.Should().Be(5);
        firstPage.Value.Items.Should().HaveCount(2);

        // Page 0 / -3 must not produce a negative Skip.
        var clamped = await handler.Handle(new ListRetentionCandidatesQuery(Page: 0, PageSize: 2),
            CancellationToken.None);

        clamped.Value.Page.Should().Be(1);
        clamped.Value.Items.Should().HaveCount(2);
    }

    [Fact]
    public async Task The_listing_is_empty_when_the_scan_has_never_run()
    {
        await SeedArchivedAsync("CLI-0013");

        await using var db = _factory.CreateContext();

        var result = await NewListHandler(db)
            .Handle(new ListRetentionCandidatesQuery(), CancellationToken.None);

        // The queue is what the scan materialized; an eligible-but-unscanned client is not in it.
        result.Value.TotalCount.Should().Be(0);
    }

    // ── Helpers ─────────────────────────────────────────────────────────────

    private ListRetentionCandidatesHandler NewListHandler(
        CustomersDbContext db, IAgencyScopeProvider? agencyScope = null) =>
        new(db,
            TestDoubles.CurrentUser(_tenantId, _userId, "Administrator"),
            agencyScope ?? TestDoubles.AgencyScope());

    private async Task<Client> SeedArchivedAsync(
        string clientNumber, string? documentNumber = "CI0123456789", string? phoneNumber = null)
    {
        await using var db = _factory.CreateContext();

        var client = ComplianceTestData.ArchivedIndividual(
            _tenantId, _agencyId, clientNumber, _userId, _encryptor, _indexer,
            documentNumber: documentNumber, phoneNumber: phoneNumber);

        db.Clients.Add(client);
        await db.SaveChangesAsync();

        return client;
    }

    private async Task RunScanAsync(bool retentionCleared, int retentionYears = 10)
    {
        var kyc = Substitute.For<IKycModule>();
        kyc.IsRetentionClearedAsync(Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(retentionCleared));

        var settings = TestDoubles.Settings(
            _tenantId, (CustomerSettingKeys.RetentionYears, retentionYears.ToString(CultureInfo.InvariantCulture)));

        await using var sp = ComplianceTestData.JobServices(
            () => _factory.CreateContext(), _clock, settings: settings, kyc: kyc);

        var job = new IdentifyRetentionCandidatesJob(sp.GetRequiredService<IServiceScopeFactory>());

        await job.ExecuteAsync(_tenantId);
    }

    private static async Task<List<ClientTimelineEntry>> TimelineEntriesAsync(CustomersDbContext db) =>
        await db.ClientTimelineEntries
            .IgnoreQueryFilters()
            .Where(e => e.EntryType == "RETENTION_ELIGIBLE")
            .ToListAsync();
}
