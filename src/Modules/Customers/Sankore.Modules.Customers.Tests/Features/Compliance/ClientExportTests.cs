namespace Sankore.Modules.Customers.Tests.Features.Compliance;

using System.Globalization;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Hangfire;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Sankore.Modules.Customers.Domain;
using Sankore.Modules.Customers.Features.Clients.SearchClients;
using Sankore.Modules.Customers.Features.Compliance.Export;
using Sankore.Modules.Customers.Features.Compliance.Export.DownloadClientExport;
using Sankore.Modules.Customers.Features.Compliance.Export.GetClientExport;
using Sankore.Modules.Customers.Features.Compliance.Export.RequestClientExport;
using Sankore.Modules.Customers.Infrastructure;
using Sankore.Modules.Customers.Tests.TestSupport;
using Sankore.Shared.Infrastructure.Crypto;
using Sankore.Shared.Kernel;
using Sankore.Shared.Kernel.Authorization;
using Xunit;

/// <summary>
/// US-M01-BE-30: request, generate, poll, download.
/// </summary>
public sealed class ClientExportTests : IDisposable
{
    private readonly Guid _tenantId = Guid.NewGuid();
    private readonly Guid _agencyId = Guid.NewGuid();
    private readonly Guid _otherAgencyId = Guid.NewGuid();
    private readonly Guid _userId = Guid.NewGuid();
    private readonly TestCustomersDbContextFactory _factory;
    private readonly IFieldEncryptor _encryptor = TestDoubles.Encryptor();
    private readonly IBlindIndexer _indexer = TestDoubles.Indexer();
    private readonly InMemoryFileStore _fileStore = new();
    /// <summary>
    /// Anchored on the real clock, not on a literal date: <c>ClientExportJob.Queue</c> stamps
    /// <c>ExpiresAt</c> from <c>DateTimeOffset.UtcNow</c> (the aggregate takes no clock), so a
    /// fixed literal would drift in and out of the TTL depending on the hour the suite runs.
    /// The expiry tests move this clock forward instead.
    /// </summary>
    private readonly FixedTimeProvider _clock = new(DateTimeOffset.UtcNow);

    private static readonly JsonSerializerOptions FilterJson = new(JsonSerializerDefaults.Web);

    private const string DocumentNumber = "CI0123456789";
    private const string PhoneNumber = "+22507112233";

    public ClientExportTests() => _factory = new TestCustomersDbContextFactory(_tenantId);

    public void Dispose() => _factory.Dispose();

    // ── Requesting ──────────────────────────────────────────────────────────

    [Fact]
    public async Task Requesting_an_export_queues_a_job_with_a_token_and_an_expiry()
    {
        await using var db = _factory.CreateContext();
        var hangfire = Substitute.For<IBackgroundJobClient>();

        var result = await NewRequestHandler(db, hangfire, ttlMinutes: 45)
            .Handle(new RequestClientExportCommand(Filters(status: ClientStatus.Active)),
                CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Status.Should().Be(nameof(ExportJobStatus.Queued));

        await using var reread = _factory.CreateContext();
        var job = await reread.ClientExportJobs.SingleAsync(j => j.Id == result.Value.ExportId);

        job.Status.Should().Be(ExportJobStatus.Queued);
        job.RequestedBy.Should().Be(_userId);
        job.RowCount.Should().Be(0);
        job.FileReference.Should().BeNull();

        // ExpiresAt = request time + export-link-ttl-minutes.
        job.ExpiresAt.Should().BeCloseTo(job.RequestedAt.AddMinutes(45), TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task The_download_token_is_a_high_entropy_url_safe_secret_that_differs_per_export()
    {
        await using var db = _factory.CreateContext();
        var hangfire = Substitute.For<IBackgroundJobClient>();
        var handler = NewRequestHandler(db, hangfire);

        var first = await handler.Handle(new RequestClientExportCommand(Filters()), CancellationToken.None);
        var second = await handler.Handle(new RequestClientExportCommand(Filters()), CancellationToken.None);

        await using var reread = _factory.CreateContext();
        var tokens = await reread.ClientExportJobs
            .Where(j => j.Id == first.Value.ExportId || j.Id == second.Value.ExportId)
            .Select(j => j.DownloadToken)
            .ToListAsync();

        tokens.Should().HaveCount(2);
        tokens.Should().OnlyHaveUniqueItems("a shared token would expose one requester's file to another");

        foreach (var token in tokens)
        {
            // At least 128 bits of entropy however it is encoded, and safe in a query string
            // without escaping (the URL is built by string concatenation).
            token.Length.Should().BeGreaterThanOrEqualTo(32);
            token.Should().MatchRegex("^[A-Za-z0-9._~-]+$");
            Uri.EscapeDataString(token).Should().Be(token);
        }
    }

    [Fact]
    public async Task Requesting_an_export_stores_the_filters_and_enqueues_the_generation_job()
    {
        await using var db = _factory.CreateContext();
        var hangfire = Substitute.For<IBackgroundJobClient>();

        var filters = Filters(name: "Kone", status: ClientStatus.Archived);

        var result = await NewRequestHandler(db, hangfire)
            .Handle(new RequestClientExportCommand(filters), CancellationToken.None);

        await using var reread = _factory.CreateContext();
        var job = await reread.ClientExportJobs.SingleAsync(j => j.Id == result.Value.ExportId);

        var stored = JsonSerializer.Deserialize<SearchClientsQuery>(job.FiltersJson, FilterJson);

        stored.Should().NotBeNull();
        stored!.Name.Should().Be("Kone");
        stored.Status.Should().Be(ClientStatus.Archived);

        // The Hangfire payload must carry identifiers only — never the filters, which can contain
        // a phone number and are persisted in clear in the job store.
        hangfire.ReceivedWithAnyArgs(1).Create(default!, default!);
    }

    [Fact]
    public void The_export_request_is_audited_and_its_payload_carries_the_filters()
    {
        var command = new RequestClientExportCommand(Filters(name: "Kone"));

        command.Should().BeAssignableTo<Sankore.Shared.Infrastructure.Behaviors.ICommand>();
        command.Should().BeAssignableTo<IResourceCommand>();
        command.ResourceType.Should().Be("ClientExportJob");
        command.ResourceId.Should().BeNull("the export id is assigned by the handler");

        // No [SensitiveData] on Filters: the audit entry has to answer "what was exported".
        var payload = JsonSerializer.Serialize(command);
        payload.Should().Contain("Kone");
    }

    // ── Generating ──────────────────────────────────────────────────────────

    [Fact]
    public async Task The_generated_csv_has_exactly_the_specified_header_line()
    {
        await SeedClientAsync("CLI-3001", _agencyId);
        var exportId = await QueueExportAsync(Filters());

        await RunGenerationAsync();

        var csv = _fileStore.SingleFileAsText();
        var header = csv.Split('\n')[0].Trim('\r');

        header.Should().Be(string.Join(',', ClientExportCsv.Headers));
        exportId.Should().NotBe(Guid.Empty);
    }

    [Fact]
    public async Task The_generated_csv_masks_the_phone_and_the_identity_document()
    {
        await SeedClientAsync("CLI-3002", _agencyId);
        await QueueExportAsync(Filters());

        await RunGenerationAsync();

        var csv = _fileStore.SingleFileAsText();

        // The clear values must be nowhere in the file — a CSV has no reveal audit and no expiry
        // once it has left the platform.
        csv.Should().NotContain(DocumentNumber);
        csv.Should().NotContain("07112233");
        csv.Should().NotContain("22507112233");

        // …and the masked renderings must be there, so the column is still useful.
        csv.Should().Contain(SensitiveValueMasker.MaskDocument(DocumentNumber)!);
        csv.Should().Contain(SensitiveValueMasker.MaskPhone(PhoneNumber)!);
    }

    [Fact]
    public async Task Generation_records_the_row_count_and_completes_the_job()
    {
        await SeedClientAsync("CLI-3003", _agencyId);
        await SeedClientAsync("CLI-3004", _agencyId);
        var exportId = await QueueExportAsync(Filters());

        await RunGenerationAsync();

        await using var db = _factory.CreateContext();
        var job = await db.ClientExportJobs.SingleAsync(j => j.Id == exportId);

        job.Status.Should().Be(ExportJobStatus.Completed);
        job.RowCount.Should().Be(2);
        job.FileReference.Should().NotBeNullOrWhiteSpace();
        job.CompletedAt.Should().Be(_clock.GetUtcNow());
        job.ErrorMessage.Should().BeNull();
    }

    [Fact]
    public async Task Generation_journals_the_row_count_on_the_timeline()
    {
        await SeedClientAsync("CLI-3005", _agencyId);
        var exportId = await QueueExportAsync(Filters());

        await RunGenerationAsync();

        await using var db = _factory.CreateContext();

        var entry = await db.ClientTimelineEntries
            .IgnoreQueryFilters()
            .SingleAsync(e => e.EntryType == "CLIENT_EXPORT_COMPLETED");

        entry.TenantId.Should().Be(_tenantId);
        entry.SourceModule.Should().Be("Customers");
        entry.ReferenceType.Should().Be("ClientExportJob");
        entry.ReferenceId.Should().Be(exportId.ToString("D"));
        entry.Summary.Should().Contain("1");

        // Tenant-level fact: an export spans many clients and belongs to no single file.
        entry.ClientId.Should().Be(Guid.Empty);
    }

    [Fact]
    public async Task Generation_reapplies_the_requesters_agency_perimeter_and_not_the_background_accounts()
    {
        // This is the escalation the US guards against: the job runs as SYSTEM, so resolving the
        // perimeter from the ambient identity would hand the requester the whole tenant.
        await SeedClientAsync("CLI-3006", _agencyId);
        await SeedClientAsync("CLI-3007", _otherAgencyId);

        var exportId = await QueueExportAsync(Filters());

        var requesterScope = Substitute.For<IAgencyScopeProvider>();
        requesterScope
            .GetAccessibleAgencyIdsAsync(_tenantId, _userId, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlySet<Guid>?>(new HashSet<Guid> { _agencyId }));

        await RunGenerationAsync(requesterScope);

        var csv = _fileStore.SingleFileAsText();
        csv.Should().Contain("CLI-3006");
        csv.Should().NotContain("CLI-3007");

        await using var db = _factory.CreateContext();
        (await db.ClientExportJobs.SingleAsync(j => j.Id == exportId)).RowCount.Should().Be(1);

        // Resolved for the requester, never for the SYSTEM account.
        await requesterScope.Received()
            .GetAccessibleAgencyIdsAsync(_tenantId, _userId, Arg.Any<CancellationToken>());
        await requesterScope.DidNotReceive()
            .GetAccessibleAgencyIdsAsync(_tenantId, Guid.Empty, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Generation_honours_the_stored_filters()
    {
        await SeedClientAsync("CLI-3008", _agencyId, firstName: "Awa", lastName: "Kone");
        await SeedClientAsync("CLI-3009", _agencyId, firstName: "Yao", lastName: "Traore");

        await QueueExportAsync(Filters(name: "Kone"));

        await RunGenerationAsync();

        var csv = _fileStore.SingleFileAsText();
        csv.Should().Contain("CLI-3008");
        csv.Should().NotContain("CLI-3009");
    }

    [Fact]
    public async Task Generation_ignores_the_clients_of_another_tenant()
    {
        await SeedClientAsync("CLI-3010", _agencyId);

        await using (var seed = _factory.CreateContext())
        {
            seed.Clients.Add(ComplianceTestData.Individual(
                Guid.NewGuid(), _agencyId, "CLI-7777", "Foreign", "Client", _userId,
                _encryptor, _indexer, DocumentNumber, PhoneNumber));

            await seed.SaveChangesAsync();
        }

        await QueueExportAsync(Filters());
        await RunGenerationAsync();

        var csv = _fileStore.SingleFileAsText();
        csv.Should().Contain("CLI-3010");
        csv.Should().NotContain("CLI-7777");
    }

    [Fact]
    public async Task A_generation_failure_marks_the_job_Failed_instead_of_leaving_it_Running()
    {
        await SeedClientAsync("CLI-3011", _agencyId);
        var exportId = await QueueExportAsync(Filters());

        var brokenStore = Substitute.For<IFileStore>();
        brokenStore.StoreAsync(Arg.Any<Stream>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns<Task<string>>(_ => throw new IOException("disk full"));

        await RunGenerationAsync(fileStore: brokenStore);

        await using var db = _factory.CreateContext();
        var job = await db.ClientExportJobs.SingleAsync(j => j.Id == exportId);

        job.Status.Should().Be(ExportJobStatus.Failed);
        job.ErrorMessage.Should().Contain("disk full");
    }

    [Fact]
    public async Task A_retried_generation_does_not_rebuild_a_completed_export()
    {
        await SeedClientAsync("CLI-3012", _agencyId);
        await QueueExportAsync(Filters());

        await RunGenerationAsync();
        await RunGenerationAsync();

        _fileStore.Files.Should().HaveCount(1);
    }

    // ── Polling ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task The_status_endpoint_returns_the_download_url_once_the_file_is_ready()
    {
        await SeedClientAsync("CLI-3013", _agencyId);
        var exportId = await QueueExportAsync(Filters());
        await RunGenerationAsync();

        await using var db = _factory.CreateContext();

        var result = await NewStatusHandler(db)
            .Handle(new GetClientExportQuery(exportId), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Status.Should().Be(nameof(ExportJobStatus.Completed));
        result.Value.RowCount.Should().Be(1);
        result.Value.IsExpired.Should().BeFalse();
        result.Value.DownloadUrl.Should().StartWith($"/api/v1/clients/exports/{exportId:D}/download?token=");
    }

    [Fact]
    public async Task The_status_endpoint_withholds_the_download_url_once_the_link_has_expired()
    {
        await SeedClientAsync("CLI-3014", _agencyId);
        var exportId = await QueueExportAsync(Filters(), ttlMinutes: 30);
        await RunGenerationAsync();

        _clock.Now = _clock.Now.AddHours(2);

        await using var db = _factory.CreateContext();

        var result = await NewStatusHandler(db)
            .Handle(new GetClientExportQuery(exportId), CancellationToken.None);

        result.Value.IsExpired.Should().BeTrue();
        result.Value.DownloadUrl.Should().BeNull();
    }

    [Fact]
    public async Task Another_user_cannot_see_someone_elses_export()
    {
        await SeedClientAsync("CLI-3015", _agencyId);
        var exportId = await QueueExportAsync(Filters());
        await RunGenerationAsync();

        await using var db = _factory.CreateContext();

        // The status payload carries the download token; handing it to a colleague would widen the
        // egress past the person the audit entry names.
        var result = await NewStatusHandler(db, asUser: Guid.NewGuid())
            .Handle(new GetClientExportQuery(exportId), CancellationToken.None);

        result.Error.Should().Be(CustomerErrors.ExportNotFound);
    }

    [Fact]
    public async Task An_unknown_export_id_answers_EXPORT_NOT_FOUND()
    {
        await using var db = _factory.CreateContext();

        var result = await NewStatusHandler(db)
            .Handle(new GetClientExportQuery(Guid.NewGuid()), CancellationToken.None);

        result.Error.Should().Be(CustomerErrors.ExportNotFound);
    }

    // ── Downloading ─────────────────────────────────────────────────────────

    [Fact]
    public async Task The_right_token_serves_the_csv()
    {
        await SeedClientAsync("CLI-3016", _agencyId);
        var exportId = await QueueExportAsync(Filters());
        await RunGenerationAsync();

        var token = await TokenOfAsync(exportId);

        await using var db = _factory.CreateContext();

        var result = await NewDownloadHandler(db)
            .Handle(new DownloadClientExportQuery(exportId, token), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.ContentType.Should().Be("text/csv; charset=utf-8");
        result.Value.FileName.Should().Be($"clients-export-{exportId:N}.csv");
        result.Value.RowCount.Should().Be(1);

        Encoding.UTF8.GetString(result.Value.Content).Should().Contain("CLI-3016");
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-the-token")]
    public async Task A_wrong_token_answers_EXPORT_NOT_FOUND_and_never_a_distinct_error(string token)
    {
        await SeedClientAsync("CLI-3017", _agencyId);
        var exportId = await QueueExportAsync(Filters());
        await RunGenerationAsync();

        await using var db = _factory.CreateContext();

        var result = await NewDownloadHandler(db)
            .Handle(new DownloadClientExportQuery(exportId, token), CancellationToken.None);

        // A distinct "invalid token" answer would confirm the export id and make this endpoint an
        // oracle a brute-force script could walk.
        result.Error.Should().Be(CustomerErrors.ExportNotFound);
    }

    [Fact]
    public async Task A_token_that_only_shares_a_prefix_is_refused()
    {
        await SeedClientAsync("CLI-3018", _agencyId);
        var exportId = await QueueExportAsync(Filters());
        await RunGenerationAsync();

        var token = await TokenOfAsync(exportId);
        var almost = token[..^1] + (token[^1] == 'a' ? 'b' : 'a');

        await using var db = _factory.CreateContext();

        var result = await NewDownloadHandler(db)
            .Handle(new DownloadClientExportQuery(exportId, almost), CancellationToken.None);

        result.Error.Should().Be(CustomerErrors.ExportNotFound);
    }

    [Fact]
    public async Task An_expired_link_answers_EXPORT_LINK_EXPIRED()
    {
        await SeedClientAsync("CLI-3019", _agencyId);
        var exportId = await QueueExportAsync(Filters(), ttlMinutes: 15);
        await RunGenerationAsync();

        var token = await TokenOfAsync(exportId);
        _clock.Now = _clock.Now.AddHours(1);

        await using var db = _factory.CreateContext();

        var result = await NewDownloadHandler(db)
            .Handle(new DownloadClientExportQuery(exportId, token), CancellationToken.None);

        // Distinct here on purpose: the caller proved they hold the token, so saying "expired"
        // reveals nothing new and saves a support ticket.
        result.Error.Should().Be(CustomerErrors.ExportLinkExpired);
    }

    [Fact]
    public async Task A_queued_export_cannot_be_downloaded_yet()
    {
        var exportId = await QueueExportAsync(Filters());
        var token = await TokenOfAsync(exportId);

        await using var db = _factory.CreateContext();

        var result = await NewDownloadHandler(db)
            .Handle(new DownloadClientExportQuery(exportId, token), CancellationToken.None);

        result.Error.Should().Be(CustomerErrors.ExportNotFound);
    }

    [Fact]
    public async Task A_completed_export_whose_file_has_vanished_answers_EXPORT_NOT_FOUND()
    {
        await SeedClientAsync("CLI-3020", _agencyId);
        var exportId = await QueueExportAsync(Filters());
        await RunGenerationAsync();

        var token = await TokenOfAsync(exportId);
        _fileStore.SimulateMissingFile = true;

        await using var db = _factory.CreateContext();

        var result = await NewDownloadHandler(db)
            .Handle(new DownloadClientExportQuery(exportId, token), CancellationToken.None);

        result.Error.Should().Be(CustomerErrors.ExportNotFound);
    }

    [Fact]
    public async Task Another_user_holding_the_token_still_cannot_download()
    {
        await SeedClientAsync("CLI-3021", _agencyId);
        var exportId = await QueueExportAsync(Filters());
        await RunGenerationAsync();

        var token = await TokenOfAsync(exportId);

        await using var db = _factory.CreateContext();

        var result = await NewDownloadHandler(db, asUser: Guid.NewGuid())
            .Handle(new DownloadClientExportQuery(exportId, token), CancellationToken.None);

        result.Error.Should().Be(CustomerErrors.ExportNotFound);
    }

    // ── Helpers ─────────────────────────────────────────────────────────────

    private static SearchClientsQuery Filters(
        string? name = null, ClientStatus? status = null, Guid? agencyId = null) =>
        new(ClientNumber: null,
            Phone: null,
            IdentityDocumentNumber: null,
            Name: name,
            Status: status,
            AgencyId: agencyId,
            AdvisorUserId: null,
            Type: null,
            SegmentCode: null);

    private RequestClientExportHandler NewRequestHandler(
        CustomersDbContext db, IBackgroundJobClient hangfire, int ttlMinutes = 60) =>
        new(db,
            TestDoubles.Settings(
                _tenantId, (CustomerSettingKeys.ExportLinkTtlMinutes, ttlMinutes.ToString(CultureInfo.InvariantCulture))),
            TestDoubles.CurrentUser(_tenantId, _userId, "Administrator"),
            hangfire,
            NullLogger<RequestClientExportHandler>.Instance);

    private GetClientExportHandler NewStatusHandler(CustomersDbContext db, Guid? asUser = null) =>
        new(db, TestDoubles.CurrentUser(_tenantId, asUser ?? _userId, "Administrator"), _clock);

    private DownloadClientExportHandler NewDownloadHandler(
        CustomersDbContext db, Guid? asUser = null) =>
        new(db,
            _fileStore,
            TestDoubles.CurrentUser(_tenantId, asUser ?? _userId, "Administrator"),
            _clock,
            NullLogger<DownloadClientExportHandler>.Instance);

    private async Task SeedClientAsync(
        string clientNumber, Guid agencyId, string firstName = "Awa", string lastName = "Kone")
    {
        await using var db = _factory.CreateContext();

        db.Clients.Add(ComplianceTestData.Individual(
            _tenantId, agencyId, clientNumber, firstName, lastName, _userId,
            _encryptor, _indexer, DocumentNumber, PhoneNumber));

        await db.SaveChangesAsync();
    }

    private async Task<Guid> QueueExportAsync(SearchClientsQuery filters, int ttlMinutes = 60)
    {
        await using var db = _factory.CreateContext();

        var result = await NewRequestHandler(db, Substitute.For<IBackgroundJobClient>(), ttlMinutes)
            .Handle(new RequestClientExportCommand(filters), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();

        return result.Value.ExportId;
    }

    private async Task RunGenerationAsync(
        IAgencyScopeProvider? agencyScope = null, IFileStore? fileStore = null)
    {
        Guid exportId;
        await using (var db = _factory.CreateContext())
        {
            exportId = await db.ClientExportJobs
                .Where(j => j.Status == ExportJobStatus.Queued || j.Status == ExportJobStatus.Running)
                .Select(j => j.Id)
                .FirstOrDefaultAsync();

            if (exportId == Guid.Empty)
            {
                // Everything is already Completed/Failed: still exercise the idempotency path.
                exportId = await db.ClientExportJobs.Select(j => j.Id).FirstAsync();
            }
        }

        await using var sp = ComplianceTestData.JobServices(
            () => _factory.CreateContext(),
            _clock,
            fileStore: fileStore ?? _fileStore,
            agencyScope: agencyScope ?? TestDoubles.AgencyScope(),
            indexer: _indexer,
            encryptor: _encryptor);

        var job = new GenerateClientExportJob(sp.GetRequiredService<IServiceScopeFactory>());

        await job.ExecuteAsync(exportId, _tenantId);
    }

    private async Task<string> TokenOfAsync(Guid exportId)
    {
        await using var db = _factory.CreateContext();

        return await db.ClientExportJobs
            .Where(j => j.Id == exportId)
            .Select(j => j.DownloadToken)
            .SingleAsync();
    }
}
