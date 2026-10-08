namespace Sankore.Modules.Kyc.Tests.Features.Files;

using FluentAssertions;
using NSubstitute;
using Sankore.Modules.Customers.PublicApi;
using Sankore.Modules.Kyc.Domain;
using Sankore.Modules.Kyc.Features.Files.ListKycFiles;
using Sankore.Modules.Kyc.Infrastructure;
using Sankore.Modules.Kyc.Tests.TestSupport;
using Sankore.Shared.Infrastructure.Auth;
using Sankore.Shared.Kernel;
using Sankore.Shared.Kernel.Authorization;
using Xunit;

/// <summary>
/// The compliance dashboard's list. Two things carry the weight: the page never reaches outside the
/// caller's agency perimeter, and "awaiting me" is a HINT drawn from roles — never a statement about
/// who may approve.
/// </summary>
public sealed class ListKycFilesHandlerTests : IDisposable
{
    private static readonly Guid MyAgency = Guid.Parse("11111111-0000-0000-0000-000000000001");
    private static readonly Guid OtherAgency = Guid.Parse("22222222-0000-0000-0000-000000000002");

    private readonly Guid _tenantId = Guid.NewGuid();
    private readonly Guid _userId = Guid.NewGuid();
    private readonly TestKycDbContextFactory _factory;
    private readonly KycDbContext _db;
    private readonly MovingClock _clock = new();

    /// <summary>
    /// What the stubbed M01 knows. A file seeded WITHOUT an entry here stands for a customer that
    /// module can no longer resolve, which is the case the null name exists for.
    /// </summary>
    private readonly Dictionary<Guid, string> _knownCustomers = [];

    public ListKycFilesHandlerTests()
    {
        _factory = new TestKycDbContextFactory(_tenantId);
        _db = _factory.CreateContext();
    }

    public void Dispose()
    {
        _db.Dispose();
        _factory.Dispose();
    }

    /// <param name="unrestricted">
    /// True stands for what IAgencyScopeProvider returns as <c>null</c>: a super-user who sees every
    /// agency. A flag rather than a nullable parameter with a <c>?? MyAgency</c> default, because
    /// that shape cannot tell "not supplied" from "explicitly unrestricted" — it reported one file
    /// instead of three and the test caught it only because the number was wrong.
    /// </param>
    private ListKycFilesHandler Handler(
        IReadOnlySet<Guid>? perimeter = null, bool unrestricted = false, params string[] roles)
        => new(_db, CurrentUser(roles), Scope(perimeter, unrestricted), Customers());

    private ICurrentUser CurrentUser(params string[] roles)
    {
        var currentUser = Substitute.For<ICurrentUser>();
        currentUser.Id.Returns(_userId);
        currentUser.TenantId.Returns(_tenantId);
        currentUser.Roles.Returns(roles);
        return currentUser;
    }

    private IAgencyScopeProvider Scope(
        IReadOnlySet<Guid>? perimeter = null, bool unrestricted = false)
    {
        var scope = Substitute.For<IAgencyScopeProvider>();
        scope.GetAccessibleAgencyIdsAsync(_tenantId, _userId, Arg.Any<CancellationToken>())
            .Returns(unrestricted ? null : perimeter ?? new HashSet<Guid> { MyAgency });
        return scope;
    }

    /// <summary>
    /// M01, stubbed to answer only about the ids it was ASKED for — the batch contract. Returning
    /// the whole registry regardless of the argument would hide the bug this shape catches: a
    /// handler keying the dictionary by anything but the requested id.
    /// </summary>
    private ICustomersModule Customers()
    {
        var customers = Substitute.For<ICustomersModule>();

        customers
            .GetClientSummariesAsync(
                _tenantId, Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(call => Task.FromResult<IReadOnlyDictionary<Guid, ClientSummary>>(
                ((IReadOnlyCollection<Guid>)call[1])
                    .Where(_knownCustomers.ContainsKey)
                    .ToDictionary(id => id, id => new ClientSummary(
                        Id: id,
                        ClientNumber: "CLI-0001",
                        ClientType: "Individual",
                        DisplayName: _knownCustomers[id],
                        Status: "Active",
                        AgencyId: MyAgency,
                        AdvisorUserId: null,
                        KycStatus: "Pending",
                        RiskLevel: "Standard",
                        MergedIntoId: null))));

        return customers;
    }

    private static ListKycFilesQuery Query(
        string? status = null, Guid? agencyId = null, string? vigilance = null,
        DateTimeOffset? from = null, DateTimeOffset? to = null, int page = 1, int pageSize = 20)
        => new(status, agencyId, vigilance, from, to, page, pageSize);

    /// <param name="withoutAgency">
    /// A file M01 could not place. Separate from <paramref name="agency"/> on purpose: a nullable
    /// parameter with a <c>?? MyAgency</c> fallback cannot tell "not supplied" from "explicitly
    /// none", and it silently filed the orphan under MyAgency — making the two perimeter tests pass
    /// for the wrong reason.
    /// </param>
    /// <param name="customerName">
    /// The name M01 will answer for this file's customer. Left null, M01 knows nothing about the
    /// id and the row must still come back.
    /// </param>
    private KycFile Seed(
        Guid? agency = null,
        bool withoutAgency = false,
        KycFileStatus status = KycFileStatus.Collecting,
        KycVigilanceLevel vigilance = KycVigilanceLevel.Standard,
        KycApprovalLevel? pendingAt = null,
        KycApprovalLevel? alreadySigned = null,
        string? customerName = null)
    {
        var customerId = Guid.NewGuid();
        if (customerName is not null) _knownCustomers[customerId] = customerName;

        var file = KycFile.Open(
            _tenantId, customerId, KycChannel.Agency, Guid.NewGuid(), _clock,
            vigilanceLevel: vigilance, agencyId: withoutAgency ? null : agency ?? MyAgency);

        // The status is set through reflection-free means only as far as the domain allows; this
        // list is a read, so the state is what matters, not how it was reached.
        typeof(KycFile).GetProperty(nameof(KycFile.Status))!.SetValue(file, status);
        _db.KycFiles.Add(file);

        if (alreadySigned is { } signed)
        {
            var step = KycApprovalStep.Pending(_tenantId, file.Id, signed, _clock);
            step.Decide(KycApprovalDecision.Approved, Guid.NewGuid(), _clock);
            _db.KycApprovalSteps.Add(step);
        }

        if (pendingAt is { } level)
            _db.KycApprovalSteps.Add(KycApprovalStep.Pending(_tenantId, file.Id, level, _clock));

        _clock.Advance(TimeSpan.FromMinutes(1));
        return file;
    }

    private async Task<KycFileListPage> ReadAsync(
        ListKycFilesHandler handler, ListKycFilesQuery? query = null)
    {
        await _db.SaveChangesAsync();
        var result = await handler.Handle(query ?? Query(), default);
        result.IsSuccess.Should().BeTrue();
        return result.Value!;
    }

    // ------------------------------------------------------------------ perimeter

    [Fact]
    public async Task The_page_never_reaches_outside_the_callers_perimeter()
    {
        Seed(agency: MyAgency);
        Seed(agency: OtherAgency);
        Seed(withoutAgency: true);

        var page = await ReadAsync(Handler());

        page.Rows.Should().ContainSingle().Which.AgencyId.Should().Be(MyAgency);
        page.TotalCount.Should().Be(1,
            "the count has to be computed after the perimeter, or the pager lies about what follows");
    }

    [Fact]
    public async Task An_unrestricted_caller_sees_every_agency_and_the_files_with_none()
    {
        Seed(agency: MyAgency);
        Seed(agency: OtherAgency);
        Seed(withoutAgency: true);

        var page = await ReadAsync(Handler(unrestricted: true));

        page.TotalCount.Should().Be(3);
    }

    [Fact]
    public async Task Asking_for_an_agency_outside_the_perimeter_returns_an_empty_page_not_a_refusal()
    {
        Seed(agency: OtherAgency);

        var page = await ReadAsync(Handler(), Query(agencyId: OtherAgency));

        page.Rows.Should().BeEmpty();
        page.TotalCount.Should().Be(0,
            "a refusal would confirm the agency exists; an empty page says nothing either way");
    }

    // ------------------------------------------------------------------ filters

    [Fact]
    public async Task Status_and_vigilance_narrow_the_list()
    {
        Seed(status: KycFileStatus.Collecting, vigilance: KycVigilanceLevel.Standard);
        Seed(status: KycFileStatus.Validating, vigilance: KycVigilanceLevel.High);

        (await ReadAsync(Handler(), Query(status: "Validating"))).TotalCount.Should().Be(1);
        (await ReadAsync(Handler(), Query(vigilance: "high"))).TotalCount.Should().Be(1);
    }

    [Fact]
    public async Task An_unknown_status_is_refused_by_name_rather_than_ignored()
    {
        Seed();
        await _db.SaveChangesAsync();

        var result = await Handler().Handle(Query(status: "Pending"), default);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain("KYC_STATUS_UNKNOWN").And.Contain("Collecting",
            "ignoring it would answer with the unfiltered list, which reads as 'none in that state'");
    }

    // ------------------------------------------------------------------ awaiting me, on roles

    [Fact]
    public async Task A_file_whose_next_rung_my_role_signs_is_flagged_and_counted()
    {
        Seed(status: KycFileStatus.Validating, pendingAt: KycApprovalLevel.BranchManager);
        Seed(status: KycFileStatus.Validating, pendingAt: KycApprovalLevel.ComplianceOfficer);

        var page = await ReadAsync(Handler(roles: Roles.BranchManager.Code));

        page.AwaitingMeCount.Should().Be(1);
        page.Rows.Count(r => r.AwaitingMe).Should().Be(1);
        page.Rows[0].AwaitingMe.Should().BeTrue("awaiting rows come first");
    }

    [Fact]
    public async Task Only_the_NEXT_rung_counts_not_any_pending_one()
    {
        // Agent still pending below the branch manager: the circuit is waiting on the agent, and
        // DecideKycApprovalHandler would refuse a branch manager signing ahead of them.
        var file = Seed(status: KycFileStatus.Validating, pendingAt: KycApprovalLevel.Agent);
        _db.KycApprovalSteps.Add(
            KycApprovalStep.Pending(_tenantId, file.Id, KycApprovalLevel.BranchManager, _clock));

        var page = await ReadAsync(Handler(roles: Roles.BranchManager.Code));

        page.AwaitingMeCount.Should().Be(0,
            "a manager must not be told a file awaits them while the agent has not signed");
    }

    [Fact]
    public async Task A_signed_rung_no_longer_counts_and_the_next_one_does()
    {
        Seed(status: KycFileStatus.Validating,
             alreadySigned: KycApprovalLevel.Agent,
             pendingAt: KycApprovalLevel.BranchManager);

        (await ReadAsync(Handler(roles: Roles.BranchManager.Code))).AwaitingMeCount.Should().Be(1);
        (await ReadAsync(Handler(roles: Roles.Agent.Code))).AwaitingMeCount.Should().Be(0);
    }

    [Fact]
    public async Task A_role_mapped_to_no_rung_lights_up_nothing()
    {
        Seed(status: KycFileStatus.Validating, pendingAt: KycApprovalLevel.Agent);

        var page = await ReadAsync(Handler(roles: Roles.Cashier.Code));

        page.AwaitingMeCount.Should().Be(0);
        page.Rows.Should().OnlyContain(r => !r.AwaitingMe,
            "an unmapped role must not light up the whole dashboard");
    }

    [Fact]
    public async Task An_administrator_answers_for_every_rung()
    {
        Seed(status: KycFileStatus.Validating, pendingAt: KycApprovalLevel.Agent);
        Seed(status: KycFileStatus.Validating, pendingAt: KycApprovalLevel.ComplianceOfficer);

        (await ReadAsync(Handler(roles: Roles.Administrator.Code))).AwaitingMeCount.Should().Be(2);
    }

    [Fact]
    public async Task The_awaiting_count_spans_the_perimeter_not_the_page()
    {
        for (var i = 0; i < 5; i++)
            Seed(status: KycFileStatus.Validating, pendingAt: KycApprovalLevel.Agent);

        var page = await ReadAsync(Handler(roles: Roles.Agent.Code), Query(pageSize: 2));

        page.Rows.Should().HaveCount(2);
        page.AwaitingMeCount.Should().Be(5, "it is a badge: it has to count the whole list");
        page.TotalCount.Should().Be(5);
    }

    [Fact]
    public async Task Within_each_group_the_file_that_has_sat_longest_comes_first()
    {
        // Seed advances the clock, so these are in increasing UpdatedAt order.
        var oldest = Seed();
        var middle = Seed();
        var newest = Seed();

        var rows = (await ReadAsync(Handler())).Rows;

        rows.Select(r => r.KycFileId).Should().Equal(
            new[] { oldest.Id, middle.Id, newest.Id },
            "a compliance worklist is a queue: the untouched file is the one that costs, so it leads "
            + "— a feed ordered newest-first would bury it");
    }

    [Fact]
    public async Task Awaiting_files_lead_regardless_of_how_recently_they_moved()
    {
        var oldUntouched = Seed();
        var recentlyAwaiting = Seed(status: KycFileStatus.Validating, pendingAt: KycApprovalLevel.Agent);

        var rows = (await ReadAsync(Handler(roles: Roles.Agent.Code))).Rows;

        rows.Select(r => r.KycFileId).Should().Equal(
            new[] { recentlyAwaiting.Id, oldUntouched.Id },
            "awaiting-me is the FIRST key: KYC-F-06 puts what needs my signature above the queue");
    }

    // ------------------------------------------------------------------ paging and actions

    [Fact]
    public async Task Paging_is_clamped_and_never_produces_a_negative_offset()
    {
        Seed();

        var page = await ReadAsync(Handler(), Query(page: 0, pageSize: 0));

        page.Page.Should().Be(1);
        page.PageSize.Should().Be(20);
        page.Rows.Should().HaveCount(1);
    }

    [Fact]
    public async Task A_page_size_beyond_the_ceiling_is_capped()
    {
        Seed();

        (await ReadAsync(Handler(), Query(pageSize: 5000))).PageSize.Should().Be(100);
    }

    [Theory]
    [InlineData(KycFileStatus.Collecting, "COLLECT_DOCUMENTS")]
    [InlineData(KycFileStatus.Validating, "AWAIT_APPROVAL")]
    [InlineData(KycFileStatus.ComplementRequired, "PROVIDE_COMPLEMENT")]
    [InlineData(KycFileStatus.Full, "NONE")]
    public async Task The_required_action_is_a_stable_code_not_a_sentence(
        KycFileStatus status, string expected)
    {
        Seed(status: status);

        var row = (await ReadAsync(Handler())).Rows.Should().ContainSingle().Subject;

        row.RequiredActionCode.Should().Be(expected);
    }

    [Fact]
    public async Task No_row_carries_anything_sensitive()
    {
        Seed(customerName: "KOUASSI Adjoua");

        var json = System.Text.Json.JsonSerializer.Serialize(await ReadAsync(Handler()));

        // The list is exported, logged and pasted into tickets. A name is fine — M01 keeps those in
        // clear so its own search stays indexable. A document number or an OCR value is not, and
        // neither reaches this projection.
        json.Should().NotContain("documentNumber").And.NotContain("ocr");
    }

    // ------------------------------------------------------------------ customer name

    [Fact]
    public async Task Each_row_carries_the_name_M01_answers_for_its_own_customer()
    {
        // Two files, so a handler zipping the two lists positionally instead of keying by id would
        // hand each row the other's name.
        Seed(customerName: "KOUASSI Adjoua");
        Seed(customerName: "DIALLO Mamadou");

        var rows = (await ReadAsync(Handler())).Rows;

        rows.Should().OnlyContain(r => r.CustomerName == _knownCustomers[r.CustomerId]);
    }

    [Fact]
    public async Task A_customer_M01_cannot_resolve_leaves_the_name_null_and_keeps_the_row()
    {
        Seed(status: KycFileStatus.Validating);

        var row = (await ReadAsync(Handler())).Rows.Should().ContainSingle().Subject;

        row.CustomerName.Should().BeNull();
        row.RequiredActionCode.Should().Be("AWAIT_APPROVAL",
            "a worklist that dropped files whose customer record vanished would hide exactly the "
            + "ones somebody has to look at");
    }

    [Fact]
    public async Task Names_cost_one_call_for_the_whole_page_not_one_per_row()
    {
        for (var i = 0; i < 5; i++) Seed(customerName: $"CLIENT {i}");

        var customers = Customers();
        var handler = new ListKycFilesHandler(_db, CurrentUser(), Scope(), customers);
        await _db.SaveChangesAsync();

        var page = (await handler.Handle(Query(pageSize: 3), default)).Value!;

        page.Rows.Should().HaveCount(3).And.OnlyContain(r => r.CustomerName != null);
        await customers.Received(1).GetClientSummariesAsync(
            _tenantId,
            // Only the page's ids: the batch is what keeps the IN list bounded by pageSize, and a
            // call carrying all five would mean the resolution is following TotalCount instead.
            Arg.Is<IReadOnlyCollection<Guid>>(ids => ids.Count == 3),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_page_with_no_rows_still_asks_M01_nothing_it_cannot_answer()
    {
        var customers = Customers();
        var handler = new ListKycFilesHandler(_db, CurrentUser(), Scope(), customers);
        await _db.SaveChangesAsync();

        var page = (await handler.Handle(Query(), default)).Value!;

        page.Rows.Should().BeEmpty();
        await customers.Received(1).GetClientSummariesAsync(
            _tenantId,
            // An empty batch, which the facade short-circuits rather than turning into `IN ()`.
            Arg.Is<IReadOnlyCollection<Guid>>(ids => ids.Count == 0),
            Arg.Any<CancellationToken>());
    }

    private sealed class MovingClock : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 10, 1, 8, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan by) => _now = _now.Add(by);
    }
}
