namespace Sankore.Modules.Integration.Tests.Features.Snapshot;

using System.Text.Json;
using System.Text.Json.Serialization;
using FluentAssertions;
using Sankore.Modules.Integration.Adapters.Fake;
using Sankore.Modules.Integration.Domain;
using Sankore.Modules.Integration.Features.Snapshot;
using Sankore.Modules.Integration.PublicApi;
using Sankore.Shared.Infrastructure.Outbox;
using Xunit;

/// <summary>
/// INT-21, the four acceptance criteria.
///
/// <para>
/// Criterion 3's reader lives in <c>IntegrationModuleFacade</c> and is another chantier's; what is
/// asserted here is the half this writer owns, and it is the dangerous half: a customer the CBS has
/// never heard of must leave NO row behind, because a row of zeros is what makes the facade answer
/// a snapshot instead of null and Customer 360 display zeros as the customer's balances.
/// </para>
/// </summary>
public sealed class CbsSnapshotProjectorTests
{
    // ── Criterion 1: the synchronisation fills the snapshot ─────────────────

    [Fact]
    public async Task Writes_accounts_loans_totals_flow_cbs_tier_and_date()
    {
        using var ctx = new SnapshotTestContext();
        ctx.SeedReference();
        ctx.SeedAcknowledgedCbsTier(KycLevel.Full);

        await ctx.ProjectAsync();

        var snapshot = ctx.Snapshot();
        snapshot.Should().NotBeNull();

        var accounts = Deserialise<CbsAccount>(snapshot!.AccountsJson);
        accounts.Should().ContainSingle()
            .Which.AccountNumber.Should().Be("CI0012345678");

        var loans = Deserialise<CbsLoan>(snapshot.LoansJson);
        loans.Should().ContainSingle()
            .Which.OutstandingAmount.Should().Be(1_450_000m);

        // The sum of what the account port answered, and nothing else.
        snapshot.TotalBalance.Should().Be(125_000m);

        // 165 000 per month (125 000 credit + 40 000 debit), over the two calendar months a
        // 30-day window ending 2026-03-11 touches.
        snapshot.MonthlyFlow.Should().Be(330_000m);

        snapshot.KycLevelInCbs.Should().Be(KycLevel.Full);
        snapshot.SnapshotAt.Should().Be(ctx.Now);
        snapshot.ConnectionId.Should().Be(SnapshotTestContext.ConnectionId);
    }

    [Fact]
    public async Task A_second_projection_updates_the_same_row_instead_of_duplicating()
    {
        using var ctx = new SnapshotTestContext();
        ctx.SeedReference();

        await ctx.ProjectAsync();

        var first = ctx.Snapshot()!.TotalBalance;

        // The CBS moved money between the two runs.
        ctx.Adapter.AccountsByCustomer[FakeAdapter.SeededCustomerId][0] =
            ctx.Adapter.AccountsByCustomer[FakeAdapter.SeededCustomerId][0] with { Balance = 300_000m };

        await ctx.ProjectAsync();

        // The composite key (tenant_id, crm_customer_id) guarantees it, but the upsert has to
        // actually find the existing row rather than Add a second one the key would reject only
        // against PostgreSQL — the InMemory provider enforces no unique index, so this assertion
        // is the only thing standing between the two.
        ctx.SnapshotCount().Should().Be(1);

        first.Should().Be(125_000m);
        ctx.Snapshot()!.TotalBalance.Should().Be(300_000m);
    }

    // ── Criterion 3: absent from the CBS is a state, not an error ───────────

    [Fact]
    public async Task A_customer_the_cbs_has_never_heard_of_creates_no_row()
    {
        using var ctx = new SnapshotTestContext();

        // No IntegrationReference: this connection has never been given an identifier for this
        // customer, which is exactly "it does not exist in the CBS".
        await ctx.ProjectAsync();

        ctx.SnapshotCount().Should().Be(0);

        // And no call was made: the absence of the reference is the answer, not something to go
        // and ask the CBS about.
        ctx.Adapter.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task A_reference_the_cbs_does_not_recognise_creates_no_row_either()
    {
        using var ctx = new SnapshotTestContext();

        // We hold a reference; the far end answers ExternalEntityNotFound for it.
        ctx.SeedReference("CUS-GONE-0404");

        await ctx.ProjectAsync();

        ctx.SnapshotCount().Should().Be(0);
        ctx.CallLog.Rows.Should().NotBeEmpty("the refusal is evidence and belongs in the journal");
    }

    // ── Criterion 2: CBS codes are translated on the write side ─────────────

    [Fact]
    public async Task Cbs_product_codes_are_stored_translated_into_crm_codes()
    {
        using var ctx = new SnapshotTestContext();
        ctx.SeedReference();
        ctx.SeedProductMapping(SnapshotTestContext.CrmSavingsCode, FakeAdapter.SeededProductCode);

        await ctx.ProjectAsync();

        var accounts = Deserialise<CbsAccount>(ctx.Snapshot()!.AccountsJson);

        accounts.Should().ContainSingle()
            .Which.ProductCode.Should().Be(SnapshotTestContext.CrmSavingsCode);
    }

    [Fact]
    public async Task An_unmapped_cbs_code_is_stored_unchanged_rather_than_lost()
    {
        using var ctx = new SnapshotTestContext();
        ctx.SeedReference();

        // No mapping row at all. Losing the code would turn "not mapped yet" into "no product",
        // which the stored row could never be recovered from.
        await ctx.ProjectAsync();

        var snapshot = ctx.Snapshot()!;

        Deserialise<CbsAccount>(snapshot.AccountsJson).Should().ContainSingle()
            .Which.ProductCode.Should().Be(FakeAdapter.SeededProductCode);

        Deserialise<CbsLoan>(snapshot.LoansJson).Should().ContainSingle()
            .Which.ProductCode.Should().Be("CREDIT-PME");
    }

    // ── Criterion 4: a divergence is reported, never corrected ─────────────

    [Fact]
    public async Task A_tier_divergence_publishes_exactly_one_mismatch_event()
    {
        using var ctx = new SnapshotTestContext();
        ctx.SeedReference();

        // M02 says Full; the last tier the CBS acknowledged is Simplified.
        ctx.WithLimits(nameof(KycLevel.Full));
        ctx.SeedAcknowledgedCbsTier(KycLevel.Simplified);

        await ctx.ProjectAsync();

        var messages = ctx.OutboxMessages();
        messages.Should().ContainSingle();

        var evt = JsonSerializer.Deserialize<CbsKycMismatchDetectedEvent>(
            messages[0].PayloadJson, OutboxJson.Options);

        evt.Should().NotBeNull();
        evt!.TenantId.Should().Be(SnapshotTestContext.Tenant);
        evt.CrmCustomerId.Should().Be(SnapshotTestContext.CrmCustomerId);
        evt.CrmLevel.Should().Be(nameof(KycLevel.Full));
        evt.CbsLevel.Should().Be(nameof(KycLevel.Simplified));

        // Reported, not corrected: nothing was queued to push a tier either way.
        ctx.Db.Commands.Should().NotContain(
            c => c.CommandType == CommandType.SetKycLevel && c.Status == CommandStatus.Pending);
    }

    [Fact]
    public async Task No_divergence_publishes_nothing()
    {
        using var ctx = new SnapshotTestContext();
        ctx.SeedReference();
        ctx.WithLimits(nameof(KycLevel.Full));
        ctx.SeedAcknowledgedCbsTier(KycLevel.Full);

        await ctx.ProjectAsync();

        ctx.Snapshot()!.KycLevelInCbs.Should().Be(KycLevel.Full);
        ctx.OutboxMessages().Should().BeEmpty();
    }

    [Fact]
    public async Task An_unknown_cbs_tier_is_not_a_divergence()
    {
        using var ctx = new SnapshotTestContext();
        ctx.SeedReference();
        ctx.WithLimits(nameof(KycLevel.Full));

        // Nothing we ever wrote to this CBS says what tier it holds.
        await ctx.ProjectAsync();

        ctx.Snapshot()!.KycLevelInCbs.Should().BeNull();
        ctx.OutboxMessages().Should().BeEmpty(
            "reporting a mismatch against every customer we never pushed a tier for would fill "
            + "the compliance queue with rows whose only content is that we do not know");
    }

    // ── Failure behaviour ───────────────────────────────────────────────────

    [Fact]
    public async Task An_adapter_failure_leaves_the_previous_snapshot_intact_and_journals_the_call()
    {
        using var ctx = new SnapshotTestContext();
        ctx.SeedReference();

        await ctx.ProjectAsync();
        var before = ctx.Snapshot()!;
        var previousBalance = before.TotalBalance;
        var previousAt = before.SnapshotAt;

        ctx.CallLog.Rows.Clear();
        ctx.Adapter.ForcedReadOutcome = IntegrationResult.Transient(IntegrationErrors.Unavailable);

        await ctx.ProjectAsync();

        var after = ctx.Snapshot()!;
        after.TotalBalance.Should().Be(previousBalance);
        after.SnapshotAt.Should().Be(previousAt, "a fresh timestamp over unread figures would lie");

        ctx.SnapshotCount().Should().Be(1);

        ctx.CallLog.Rows.Should().ContainSingle()
            .Which.ErrorCode.Should().Be(IntegrationErrors.Unavailable);
    }

    [Fact]
    public async Task A_failed_flow_read_discards_accounts_and_loans_that_did_answer()
    {
        using var ctx = new SnapshotTestContext();
        ctx.SeedReference();

        // Accounts and loans answer; only the flow fails. The partial snapshot is the tempting
        // one to keep — and the one that must not be kept: a monthly flow stored low is compared
        // against a compliance ceiling, and the accounts beside it would make the row look read.
        ctx.FailTheFlowRead();

        await ctx.ProjectAsync();

        ctx.SnapshotCount().Should().Be(0);

        ctx.Adapter.Calls.Should().Contain(c => c.Operation == FakeAdapterOperations.GetAccounts);
        ctx.Adapter.Calls.Should().Contain(c => c.Operation == FakeAdapterOperations.GetLoans);
    }

    // ── The flow window is M02's setting, not a literal ────────────────────

    [Theory]
    [InlineData(30, 2)]
    [InlineData(90, 4)]
    [InlineData(1, 1)]
    public async Task The_flow_window_comes_from_the_kyc_module(int windowDays, int expectedMonthReads)
    {
        using var ctx = new SnapshotTestContext();
        ctx.SeedReference();
        ctx.WithLimits(nameof(KycLevel.Full), windowDays);

        await ctx.ProjectAsync();

        // One GetMonthlyFlow call per calendar month the window touches. A hard-coded 30 would
        // answer 2 for every row of this table.
        ctx.Adapter.Calls
            .Count(c => c.Operation == FakeAdapterOperations.GetMonthlyFlow)
            .Should().Be(expectedMonthReads);

        ctx.Snapshot()!.MonthlyFlow.Should().Be(165_000m * expectedMonthReads);
    }

    [Fact]
    public async Task A_customer_with_no_kyc_file_is_measured_over_the_current_month()
    {
        using var ctx = new SnapshotTestContext();
        ctx.SeedReference();

        // M02 has no file: there is no window to read, and the column's own definition — a
        // MONTHLY flow, read through a month-granular port — is the fallback. No day count is
        // invented here.
        ctx.WithLimits(tier: null);

        await ctx.ProjectAsync();

        ctx.Adapter.Calls
            .Count(c => c.Operation == FakeAdapterOperations.GetMonthlyFlow)
            .Should().Be(1);

        ctx.Snapshot()!.MonthlyFlow.Should().Be(165_000m);
    }

    [Fact]
    public async Task No_kyc_file_reads_as_level_None_and_diverges_from_an_acknowledged_tier()
    {
        using var ctx = new SnapshotTestContext();
        ctx.SeedReference();
        ctx.WithLimits(tier: null);
        ctx.SeedAcknowledgedCbsTier(KycLevel.Simplified);

        await ctx.ProjectAsync();

        var evt = JsonSerializer.Deserialize<CbsKycMismatchDetectedEvent>(
            ctx.OutboxMessages().Should().ContainSingle().Which.PayloadJson, OutboxJson.Options);

        evt!.CrmLevel.Should().Be(nameof(KycLevel.None));
        evt.CbsLevel.Should().Be(nameof(KycLevel.Simplified));
    }

    // ── The jsonb round trip ───────────────────────────────────────────────

    [Fact]
    public async Task The_stored_json_is_readable_with_the_options_the_facade_reads_with()
    {
        using var ctx = new SnapshotTestContext();
        ctx.SeedReference();

        await ctx.ProjectAsync();

        var snapshot = ctx.Snapshot()!;

        // camelCase, the facade's own policy. Asserted on the raw text because the failure this
        // guards against is silent: a PascalCase column deserialises to a complete-looking list of
        // zeros, with nothing in the logs.
        snapshot.AccountsJson.Should().Contain("\"accountNumber\"");
        snapshot.LoansJson.Should().Contain("\"outstandingAmount\"");

        // Enums as names, so a renumbering cannot re-interpret rows already written.
        snapshot.AccountsJson.Should().NotContain("\"status\":0");

        var reread = Deserialise<CbsAccount>(snapshot.AccountsJson);
        reread[0].AccountId.Value.Should().Be(FakeAdapter.SeededAccountId);
        reread[0].Currency.Should().Be(FakeAdapter.SeededCurrency);
        reread[0].OpenedOn.Should().Be(new DateOnly(2022, 6, 1));
    }

    /// <summary>
    /// Reads a jsonb column exactly as <c>IntegrationModuleFacade</c> does — case-insensitive
    /// camelCase with enums as names — so the assertions above fail if the two sides ever diverge.
    /// </summary>
    private static List<T> Deserialise<T>(string json)
        => JsonSerializer.Deserialize<List<T>>(json, FacadeReaderOptions) ?? [];

    private static readonly JsonSerializerOptions FacadeReaderOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() },
    };
}

/// <summary>
/// The window arithmetic on its own, because the projector test can only observe it through the
/// number of calls the fake recorded.
/// </summary>
public sealed class SnapshotFlowWindowTests
{
    private static readonly DateTimeOffset Mid = new(2026, 3, 11, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public void A_thirty_day_window_spans_the_two_months_it_touches()
        => SnapshotFlowWindow.MonthsEndingAt(Mid, 30)
            .Should().Equal(new YearMonth(2026, 2), new YearMonth(2026, 3));

    [Fact]
    public void A_ninety_day_window_spans_four()
        => SnapshotFlowWindow.MonthsEndingAt(Mid, 90)
            .Should().Equal(
                new YearMonth(2025, 12), new YearMonth(2026, 1),
                new YearMonth(2026, 2), new YearMonth(2026, 3));

    [Fact]
    public void A_one_day_window_is_today_alone()
        => SnapshotFlowWindow.MonthsEndingAt(Mid, 1)
            .Should().Equal(new YearMonth(2026, 3));

    [Theory]
    [InlineData(null)]
    [InlineData(0)]
    [InlineData(-5)]
    public void An_unknown_window_falls_back_to_the_current_calendar_month(int? windowDays)
        => SnapshotFlowWindow.MonthsEndingAt(Mid, windowDays)
            .Should().Equal(new YearMonth(2026, 3));

    [Fact]
    public void A_window_that_crosses_a_year_boundary_still_enumerates_in_order()
        => SnapshotFlowWindow.MonthsEndingAt(new DateTimeOffset(2026, 1, 3, 0, 0, 0, TimeSpan.Zero), 30)
            .Should().Equal(new YearMonth(2025, 12), new YearMonth(2026, 1));

    // ── The optional read port (INT-21 criterion 1, via ICbsKycLevelPort) ───────────────────

    [Fact]
    public async Task The_cbs_tier_comes_from_the_port_when_the_adapter_declares_it()
    {
        using var ctx = new SnapshotTestContext();
        ctx.SeedReference();

        // M02 says Simplified; the CBS itself reports Full. This is the case the inference can
        // never see — a tier changed inside the CBS, with no command of ours behind it — and it
        // is the one criterion 4 exists for.
        ctx.WithLimits(nameof(KycLevel.Simplified));
        ctx.SeedCbsReportedTier(KycLevel.Full);

        await ctx.ProjectAsync();

        var snapshot = ctx.Snapshot();
        snapshot!.KycLevelInCbs.Should().Be(KycLevel.Full);

        var messages = ctx.OutboxMessages();
        messages.Should().ContainSingle("the two sides disagree and that is reported, not fixed");
    }

    [Fact]
    public async Task The_port_wins_over_the_inference_when_the_two_disagree()
    {
        using var ctx = new SnapshotTestContext();
        ctx.SeedReference();
        ctx.WithLimits(nameof(KycLevel.Full));

        // An acknowledged write says Simplified, the CBS now reports Full. SeedAcknowledgedCbsTier
        // drops the capability by construction, so re-enable the port afterwards to build the
        // genuinely ambiguous fixture.
        ctx.SeedAcknowledgedCbsTier(KycLevel.Simplified);
        ctx.Adapter.Capabilities = IntegrationCapabilities.All(
            CapabilityMode.RealTime, Enum.GetValues<IntegrationCapability>());
        ctx.SeedCbsReportedTier(KycLevel.Full);

        await ctx.ProjectAsync();

        var snapshot = ctx.Snapshot();

        // What the CBS holds, not what it last accepted from us: the second is only ever evidence
        // about our own queue.
        snapshot!.KycLevelInCbs.Should().Be(KycLevel.Full);
        ctx.OutboxMessages().Should().BeEmpty("M02 and the CBS now agree on Full");
    }

    [Fact]
    public async Task A_failing_port_records_no_tier_rather_than_falling_back_to_the_inference()
    {
        using var ctx = new SnapshotTestContext();
        ctx.SeedReference();
        ctx.WithLimits(nameof(KycLevel.Full));
        ctx.SeedAcknowledgedCbsTier(KycLevel.Simplified);

        // Re-enable the port, then break every read on it.
        ctx.Adapter.Capabilities = IntegrationCapabilities.All(
            CapabilityMode.RealTime, Enum.GetValues<IntegrationCapability>());
        ctx.Adapter.ForcedReadOutcome = IntegrationResult.Transient(IntegrationErrors.Timeout);

        await ctx.ProjectAsync();

        var snapshot = ctx.Snapshot();

        // Null and NOT the acknowledged Simplified. The two answer different questions, and
        // substituting one for the other would report a divergence as resolved when the read
        // merely failed.
        snapshot?.KycLevelInCbs.Should().BeNull();
    }
}
