namespace Sankore.Modules.Integration.Tests.Features.Reconciliation;

using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Sankore.Modules.Customers.PublicApi;
using Sankore.Modules.Integration.Domain;
using Sankore.Modules.Integration.Features.Reconciliation;
using Sankore.Modules.Integration.Infrastructure;
using Sankore.Modules.Integration.PublicApi;
using Sankore.Modules.Integration.Tests.Features.Commands;
using Sankore.Modules.Kyc.PublicApi;
using Xunit;

/// <summary>
/// The daily comparison itself (INT-34, criteria 1 to 4). Each test runs the sweep the way a
/// night does — over a NAMED InMemory database, so a second run sees what the first wrote.
/// </summary>
public sealed class ReconcileTenantJobTests
{
    private static readonly Guid CustomerA = new("11111111-0000-0000-0000-000000000001");
    private static readonly Guid CustomerB = new("22222222-0000-0000-0000-000000000002");

    // ── Criterion 2: the four detections ────────────────────────────────────

    [Fact]
    public async Task Records_a_MissingInExternal_gap_for_an_active_client_with_no_snapshot()
    {
        var databaseName = Guid.NewGuid().ToString();

        await Seed(databaseName, references: [(CustomerA, "EXT-001")], snapshots: []);

        var report = await Run(
            databaseName,
            ReconciliationTestContext.NightOne,
            ReconciliationTestContext.Customers(ReconciliationTestContext.Client(CustomerA)),
            ReconciliationTestContext.Kyc("Full"));

        report.Checked.Should().Be(1);
        report.New.Should().Be(1);

        var gap = await SingleGap(databaseName);

        gap.GapType.Should().Be(GapType.MissingInExternal);
        gap.CrmId.Should().Be(CustomerA);

        // No external identifier on this type, per the aggregate's field contract. The id is
        // recoverable through GET integration/references; an external customer number on a row
        // that gets exported is an identifier of a person at a bank.
        gap.ExternalId.Should().BeNull();
    }

    [Fact]
    public async Task Records_a_KycMismatch_gap_when_the_two_tiers_disagree()
    {
        var databaseName = Guid.NewGuid().ToString();

        await Seed(
            databaseName,
            references: [(CustomerA, "EXT-001")],
            snapshots: [(CustomerA, KycLevel.Simplified)]);

        await Run(
            databaseName,
            ReconciliationTestContext.NightOne,
            ReconciliationTestContext.Customers(ReconciliationTestContext.Client(CustomerA)),
            ReconciliationTestContext.Kyc("Full"));

        var gap = await SingleGap(databaseName);

        gap.GapType.Should().Be(GapType.KycMismatch);
        gap.ExternalId.Should().Be("EXT-001");
        gap.DetailsJson.Should().Contain("Simplified");
    }

    [Fact]
    public async Task Records_a_StatusMismatch_gap_when_the_CRM_has_retired_a_customer_the_CBS_keeps()
    {
        var databaseName = Guid.NewGuid().ToString();

        await Seed(
            databaseName,
            references: [(CustomerA, "EXT-001")],
            snapshots: [(CustomerA, KycLevel.Full)]);

        await Run(
            databaseName,
            ReconciliationTestContext.NightOne,
            ReconciliationTestContext.Customers(
                ReconciliationTestContext.Client(CustomerA, status: "Suspended")),
            ReconciliationTestContext.Kyc("Full"));

        var gap = await SingleGap(databaseName);

        gap.GapType.Should().Be(GapType.StatusMismatch);
    }

    [Fact]
    public async Task Records_nothing_when_both_sides_agree()
    {
        var databaseName = Guid.NewGuid().ToString();

        await Seed(
            databaseName,
            references: [(CustomerA, "EXT-001")],
            snapshots: [(CustomerA, KycLevel.Full)]);

        var report = await Run(
            databaseName,
            ReconciliationTestContext.NightOne,
            ReconciliationTestContext.Customers(ReconciliationTestContext.Client(CustomerA)),
            ReconciliationTestContext.Kyc("Full"));

        report.Checked.Should().Be(1);
        report.New.Should().Be(0);

        await using var db = ReconciliationTestContext.NewDb(
            ReconciliationTestContext.Tenant, databaseName);

        db.ReconciliationGaps.Should().BeEmpty();

        // And nothing is mailed about a clean night: criterion 4 is "lorsqu'il y a de nouveaux
        // écarts". A summary sent regardless would be ignored by the end of the month, and the one
        // night it mattered would look like the twenty-nine before it.
        ReconciliationTestContext.Published<ReconciliationCompletedEvent>(db).Should().BeEmpty();
    }

    // ── Criterion 3: dedup across nights, and automatic closure ─────────────

    [Fact]
    public async Task A_gap_still_true_the_next_night_is_one_row_whose_detection_date_does_not_move()
    {
        var databaseName = Guid.NewGuid().ToString();

        await Seed(databaseName, references: [(CustomerA, "EXT-001")], snapshots: []);

        var customers = ReconciliationTestContext.Customers(
            ReconciliationTestContext.Client(CustomerA));
        var kyc = ReconciliationTestContext.Kyc("Full");

        await Run(databaseName, ReconciliationTestContext.NightOne, customers, kyc);
        var second = await Run(databaseName, ReconciliationTestContext.NightTwo, customers, kyc);

        // The failure mode this pins: a single unresolved divergence becoming one row per night,
        // which turns the ledger into three hundred rows a year and stops it being readable — the
        // aggregate's own stated reason for the dedup key.
        var gaps = await AllGaps(databaseName);
        gaps.Should().HaveCount(1);

        gaps[0].DetectedAt.Should().Be(ReconciliationTestContext.NightOne);
        gaps[0].LastSeenAt.Should().Be(ReconciliationTestContext.NightTwo);
        gaps[0].Resolution.Should().Be(GapResolution.Open);

        second.New.Should().Be(0);
        second.Recurring.Should().Be(1);

        // Still only ONE summary in the outbox: the second night had no new gaps.
        await using var db = ReconciliationTestContext.NewDb(
            ReconciliationTestContext.Tenant, databaseName);

        ReconciliationTestContext.Published<ReconciliationCompletedEvent>(db).Should().HaveCount(1);
    }

    [Fact]
    public async Task A_gap_that_has_disappeared_is_closed_automatically_with_no_actor_and_no_note()
    {
        var databaseName = Guid.NewGuid().ToString();

        await Seed(databaseName, references: [(CustomerA, "EXT-001")], snapshots: []);

        var customers = ReconciliationTestContext.Customers(
            ReconciliationTestContext.Client(CustomerA));
        var kyc = ReconciliationTestContext.Kyc("Full");

        await Run(databaseName, ReconciliationTestContext.NightOne, customers, kyc);

        // The CBS now holds the customer: the divergence is over.
        await using (var fix = ReconciliationTestContext.NewDb(
            ReconciliationTestContext.Tenant, databaseName))
        {
            fix.CbsSnapshots.Add(ReconciliationTestContext.Snapshot(
                ReconciliationTestContext.Tenant, CustomerA, KycLevel.Full));
            await fix.SaveChangesAsync();
        }

        var second = await Run(databaseName, ReconciliationTestContext.NightTwo, customers, kyc);

        second.Closed.Should().Be(1);

        var gap = await SingleGap(databaseName, GapResolution.Closed);

        gap.Resolution.Should().Be(GapResolution.Closed);
        gap.ResolvedAt.Should().Be(ReconciliationTestContext.NightTwo);

        // Nobody decided it, it stopped being true: no actor and no note, which is what
        // distinguishes an automatic closure from a human resolution in the ledger.
        gap.ResolvedBy.Should().BeNull();
        gap.ResolutionNote.Should().BeNull();
    }

    [Fact]
    public async Task A_resolved_gap_that_is_still_true_is_re_opened_as_a_new_row()
    {
        var databaseName = Guid.NewGuid().ToString();

        await Seed(databaseName, references: [(CustomerA, "EXT-001")], snapshots: []);

        var customers = ReconciliationTestContext.Customers(
            ReconciliationTestContext.Client(CustomerA));
        var kyc = ReconciliationTestContext.Kyc("Full");

        await Run(databaseName, ReconciliationTestContext.NightOne, customers, kyc);

        // An operator closes it by hand without the divergence actually going away — which is
        // why the unique index is filtered on the OPEN rows: the same gap may legitimately
        // reappear, and it must come back with its own detection date rather than resurrect a
        // signed-off row.
        await using (var fix = ReconciliationTestContext.NewDb(
            ReconciliationTestContext.Tenant, databaseName))
        {
            var gap = await fix.ReconciliationGaps.AsTracking().SingleAsync();
            gap.Resolve(Guid.NewGuid(), "Handled in the CBS.",
                new ReconciliationTestContext.FixedClock(ReconciliationTestContext.NightOne));
            await fix.SaveChangesAsync();
        }

        var second = await Run(databaseName, ReconciliationTestContext.NightTwo, customers, kyc);

        second.New.Should().Be(1);

        var gaps = await AllGaps(databaseName);
        gaps.Should().HaveCount(2);
        gaps.Should().ContainSingle(g => g.Resolution == GapResolution.Resolved);

        var reopened = gaps.Single(g => g.Resolution == GapResolution.Open);
        reopened.DetectedAt.Should().Be(ReconciliationTestContext.NightTwo);
    }

    [Fact]
    public async Task A_gap_of_an_undetectable_type_is_never_closed_automatically()
    {
        // The reason ReconciliationScope.Detected is the auto-close filter and not decoration.
        // Without it, a run that structurally cannot look for MissingInCrm would close every open
        // row of that type on the grounds that it did not find one — silently signing off
        // findings nobody looked at.
        var databaseName = Guid.NewGuid().ToString();

        await Seed(databaseName, references: [(CustomerA, "EXT-001")], snapshots: []);

        await using (var fix = ReconciliationTestContext.NewDb(
            ReconciliationTestContext.Tenant, databaseName))
        {
            fix.ReconciliationGaps.Add(IntegrationReconciliationGap.Open(
                tenantId: ReconciliationTestContext.Tenant,
                runId: Guid.NewGuid(),
                connectionId: ReconciliationTestContext.ConnectionId,
                gapType: GapType.MissingInCrm,
                crmId: null,
                externalId: "EXT-ORPHAN",
                clock: new ReconciliationTestContext.FixedClock(ReconciliationTestContext.NightOne)));

            await fix.SaveChangesAsync();
        }

        var report = await Run(
            databaseName,
            ReconciliationTestContext.NightTwo,
            ReconciliationTestContext.Customers(ReconciliationTestContext.Client(CustomerA)),
            ReconciliationTestContext.Kyc("Full"));

        report.Closed.Should().Be(0);

        var gaps = await AllGaps(databaseName);
        gaps.Single(g => g.GapType == GapType.MissingInCrm)
            .Resolution.Should().Be(GapResolution.Open);
    }

    // ── Criterion 1 and the run row ─────────────────────────────────────────

    [Fact]
    public async Task Writes_a_run_row_that_states_which_gap_types_it_could_not_look_for()
    {
        var databaseName = Guid.NewGuid().ToString();

        await Seed(databaseName, references: [(CustomerA, "EXT-001")], snapshots: []);

        await Run(
            databaseName,
            ReconciliationTestContext.NightOne,
            ReconciliationTestContext.Customers(ReconciliationTestContext.Client(CustomerA)),
            ReconciliationTestContext.Kyc("Full"));

        await using var db = ReconciliationTestContext.NewDb(
            ReconciliationTestContext.Tenant, databaseName);

        var run = await db.ReconciliationRuns.SingleAsync();

        run.StartedAt.Should().Be(ReconciliationTestContext.NightOne);
        run.FinishedAt.Should().Be(ReconciliationTestContext.NightOne);
        run.CheckedCount.Should().Be(1);
        run.GapCount.Should().Be(1);
        run.ClosedCount.Should().Be(0);
        run.FailureDetail.Should().BeNull();

        // The visible half of design decision 1: a gap type that can never fire must not read
        // like one that fired and found nothing. If this is ever null, a zero MissingInCrm count
        // on a compliance report becomes indistinguishable from a measurement.
        run.UndetectableGapTypes.Should().Be(nameof(GapType.MissingInCrm));
    }

    [Fact]
    public async Task Records_a_failed_run_rather_than_leaving_the_night_looking_untouched()
    {
        // A crashed comparison is itself a finding: IntegrationReconciliationRun exists so the
        // control function can show the comparison ran every day. Without the row written before
        // the sweep and failed after, a night that died would be indistinguishable from a night
        // that never ran.
        var databaseName = Guid.NewGuid().ToString();

        await Seed(databaseName, references: [(CustomerA, "EXT-001")], snapshots: []);

        var customers = Substitute.For<ICustomersModule>();
        customers.GetClientSummariesAsync(
                Arg.Any<Guid>(), Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .ThrowsAsyncForAnyArgs(new InvalidOperationException("M01 unavailable"));

        var report = await Run(
            databaseName, ReconciliationTestContext.NightOne, customers,
            ReconciliationTestContext.Kyc("Full"));

        report.Failed.Should().Be(1);

        await using var db = ReconciliationTestContext.NewDb(
            ReconciliationTestContext.Tenant, databaseName);

        var run = await db.ReconciliationRuns.SingleAsync();

        run.FinishedAt.Should().NotBeNull();
        run.FailureDetail.Should().NotBeNullOrWhiteSpace();

        // Nothing half-built was committed, and no summary was mailed about a comparison that
        // did not complete.
        db.ReconciliationGaps.Should().BeEmpty();
        ReconciliationTestContext.Published<ReconciliationCompletedEvent>(db).Should().BeEmpty();
    }

    // ── Criterion 4: the summary ────────────────────────────────────────────

    [Fact]
    public async Task Publishes_one_summary_carrying_counts_per_type_and_no_customer_identity()
    {
        var databaseName = Guid.NewGuid().ToString();

        await Seed(
            databaseName,
            references: [(CustomerA, "EXT-001"), (CustomerB, "EXT-002")],
            snapshots: [(CustomerB, KycLevel.Simplified)]);

        await Run(
            databaseName,
            ReconciliationTestContext.NightOne,
            ReconciliationTestContext.Customers(
                ReconciliationTestContext.Client(CustomerA),
                ReconciliationTestContext.Client(CustomerB)),
            ReconciliationTestContext.Kyc("Full"));

        await using var db = ReconciliationTestContext.NewDb(
            ReconciliationTestContext.Tenant, databaseName);

        var summary = ReconciliationTestContext
            .Published<ReconciliationCompletedEvent>(db).Should().ContainSingle().Subject;

        summary.TenantId.Should().Be(ReconciliationTestContext.Tenant);
        summary.ConnectionId.Should().Be(ReconciliationTestContext.ConnectionId);
        summary.CheckedCount.Should().Be(2);
        summary.NewGapCount.Should().Be(2);

        summary.GapsByType.Should().BeEquivalentTo(new Dictionary<string, int>
        {
            [nameof(GapType.MissingInExternal)] = 1,
            [nameof(GapType.KycMismatch)] = 1,
        });

        // A summary is counts. An administrator's notification must name no customer, so the
        // serialised payload carries neither reference.
        var payload = db.OutboxMessages.Single().PayloadJson;
        payload.Should().NotContain(CustomerA.ToString());
        payload.Should().NotContain(CustomerB.ToString());
        payload.Should().NotContain("EXT-001");
    }

    [Fact]
    public async Task The_summary_and_the_gaps_commit_together()
    {
        // The outbox publisher writes into the job's own context without saving, so one
        // SaveChangesAsync is what makes the ledger and its notification atomic. Published first
        // and recorded after, a crash in between would mail a summary of gaps nobody can see.
        var databaseName = Guid.NewGuid().ToString();

        await Seed(databaseName, references: [(CustomerA, "EXT-001")], snapshots: []);

        await Run(
            databaseName,
            ReconciliationTestContext.NightOne,
            ReconciliationTestContext.Customers(ReconciliationTestContext.Client(CustomerA)),
            ReconciliationTestContext.Kyc("Full"));

        await using var db = ReconciliationTestContext.NewDb(
            ReconciliationTestContext.Tenant, databaseName);

        var gapCount = await db.ReconciliationGaps.CountAsync();
        var summary = ReconciliationTestContext.Published<ReconciliationCompletedEvent>(db).Single();

        summary.NewGapCount.Should().Be(gapCount);
    }

    // ── Scope: which connections, which tenants, which references ───────────

    [Fact]
    public async Task Reconciles_only_active_core_banking_connections()
    {
        // An insurance connection writes no cbs_customer_snapshot, so comparing one would report
        // its entire portfolio as MissingInExternal against a read model that was never meant to
        // describe it. A deactivated connection is frozen history whose snapshot stopped being
        // refreshed.
        var databaseName = Guid.NewGuid().ToString();
        var insuranceId = new Guid("eeeeeeee-0000-0000-0000-00000000000e");
        var inactiveId = new Guid("ffffffff-0000-0000-0000-00000000000f");

        await using (var seed = ReconciliationTestContext.NewDb(
            ReconciliationTestContext.Tenant, databaseName))
        {
            seed.Connections.Add(CommandsTestHarness.Connection(
                ReconciliationTestContext.Tenant,
                family: IntegrationFamily.Insurance,
                id: insuranceId));

            seed.Connections.Add(CommandsTestHarness.Connection(
                ReconciliationTestContext.Tenant, active: false, id: inactiveId));

            seed.References.Add(ReconciliationTestContext.Reference(
                ReconciliationTestContext.Tenant, CustomerA, "EXT-001", insuranceId));

            seed.References.Add(ReconciliationTestContext.Reference(
                ReconciliationTestContext.Tenant, CustomerB, "EXT-002", inactiveId));

            await seed.SaveChangesAsync();
        }

        var report = await Run(
            databaseName,
            ReconciliationTestContext.NightOne,
            ReconciliationTestContext.Customers(
                ReconciliationTestContext.Client(CustomerA),
                ReconciliationTestContext.Client(CustomerB)),
            ReconciliationTestContext.Kyc("Full"));

        report.Connections.Should().Be(0);
        report.Checked.Should().Be(0);

        await using var db = ReconciliationTestContext.NewDb(
            ReconciliationTestContext.Tenant, databaseName);

        db.ReconciliationRuns.Should().BeEmpty();
        db.ReconciliationGaps.Should().BeEmpty();
    }

    [Fact]
    public async Task One_tenants_comparison_never_reads_or_writes_anothers()
    {
        var databaseName = Guid.NewGuid().ToString();

        // Both tenants hold the SAME divergence over the SAME customer id and the same external
        // id — the shape that would be reconciled twice, or attributed to the wrong IMF, if any
        // predicate here were missing its tenant clause. The connection ids differ because
        // integration_connection is keyed by id alone, so one id cannot belong to two tenants.
        var otherConnection = new Guid("0a0a0a0a-0000-0000-0000-00000000000a");

        await Seed(databaseName, references: [(CustomerA, "EXT-001")], snapshots: []);

        await using (var other = ReconciliationTestContext.NewDb(
            ReconciliationTestContext.OtherTenant, databaseName))
        {
            other.Connections.Add(ReconciliationTestContext.Connection(
                ReconciliationTestContext.OtherTenant, otherConnection));
            other.References.Add(ReconciliationTestContext.Reference(
                ReconciliationTestContext.OtherTenant, CustomerA, "EXT-001", otherConnection));
            await other.SaveChangesAsync();
        }

        var report = await Run(
            databaseName,
            ReconciliationTestContext.NightOne,
            ReconciliationTestContext.Customers(ReconciliationTestContext.Client(CustomerA)),
            ReconciliationTestContext.Kyc("Full"));

        report.Checked.Should().Be(1);

        await using var victim = ReconciliationTestContext.NewDb(
            ReconciliationTestContext.OtherTenant, databaseName);

        victim.ReconciliationRuns.Should().BeEmpty();
        victim.ReconciliationGaps.Should().BeEmpty();
    }

    [Fact]
    public async Task A_reference_whose_CRM_record_M01_does_not_know_is_reported_and_not_turned_into_a_gap()
    {
        var databaseName = Guid.NewGuid().ToString();

        await Seed(databaseName, references: [(CustomerA, "EXT-001")], snapshots: []);

        var logger = new ReconciliationTestContext.RecordingLogger();

        await using var db = ReconciliationTestContext.NewDb(
            ReconciliationTestContext.Tenant, databaseName);

        var report = await ReconcileTenantJob.RunAsync(
            db,
            // M01 answers nothing for this id.
            ReconciliationTestContext.Customers(),
            ReconciliationTestContext.Kyc("Full"),
            ReconciliationTestContext.Publisher(db),
            new ReconciliationTestContext.FixedClock(ReconciliationTestContext.NightOne),
            logger,
            ReconciliationTestContext.Tenant,
            CancellationToken.None);

        report.UnknownInCrm.Should().Be(1);
        report.New.Should().Be(0);

        // Reported rather than dropped in silence: M01 deletes no client, so this is a
        // data-integrity anomaly somebody has to see. Turning it into MissingInCrm would give
        // that gap type a detector that in practice never fires.
        logger.Entries.Should().Contain(e => e.Level == LogLevel.Warning);
    }

    [Fact]
    public async Task Walks_every_reference_across_page_boundaries()
    {
        // Keyset paging is what the whole comparison rests on: a page boundary that skipped a
        // reference would under-report divergences silently. The fixture is deliberately larger
        // than one page and not a multiple of it, so both the "short last page" exit and the
        // cursor advance are exercised.
        var databaseName = Guid.NewGuid().ToString();
        var count = (ReconcileTenantJob.PageSize * 2) + 7;

        var customers = new List<ClientSummary>(count);
        var references = new List<(Guid, string)>(count);

        for (var i = 0; i < count; i++)
        {
            var id = Guid.NewGuid();
            customers.Add(ReconciliationTestContext.Client(id));

            // Zero-padded so the lexical order of external_id is well defined, which is what the
            // cursor orders on.
            references.Add((id, $"EXT-{i:D5}"));
        }

        await Seed(databaseName, references, snapshots: []);

        var report = await Run(
            databaseName,
            ReconciliationTestContext.NightOne,
            ReconciliationTestContext.Customers([.. customers]),
            ReconciliationTestContext.Kyc("Full"));

        report.Checked.Should().Be(count);
        report.New.Should().Be(count);

        (await AllGaps(databaseName)).Should().HaveCount(count);
    }

    [Fact]
    public async Task Asks_M01_one_batch_per_page_and_never_one_call_per_customer()
    {
        // ICustomersModule.GetClientSummariesAsync turns its argument into a single SQL IN and its
        // contract says a caller must keep the batch to a page's worth of ids. This pins both
        // halves: the batch read is used at all, and no page hands it more than PageSize ids.
        var databaseName = Guid.NewGuid().ToString();
        var count = ReconcileTenantJob.PageSize + 5;

        var customers = new List<ClientSummary>(count);
        var references = new List<(Guid, string)>(count);

        for (var i = 0; i < count; i++)
        {
            var id = Guid.NewGuid();
            customers.Add(ReconciliationTestContext.Client(id));
            references.Add((id, $"EXT-{i:D5}"));
        }

        await Seed(databaseName, references, snapshots: []);

        var m01 = ReconciliationTestContext.Customers([.. customers]);

        await Run(
            databaseName, ReconciliationTestContext.NightOne, m01,
            ReconciliationTestContext.Kyc("Full"));

        var batches = m01.ReceivedCalls()
            .Where(c => c.GetMethodInfo().Name == nameof(ICustomersModule.GetClientSummariesAsync))
            .Select(c => (IReadOnlyCollection<Guid>)c.GetArguments()[1]!)
            .ToList();

        batches.Should().HaveCount(2);
        batches.Should().OnlyContain(b => b.Count <= ReconcileTenantJob.PageSize);
        batches.Sum(b => b.Count).Should().Be(count);

        // And the per-customer single read is never used for this.
        await m01.DidNotReceiveWithAnyArgs()
            .GetClientSummaryAsync(default, default, default);
    }

    [Fact]
    public async Task A_customer_M02_cannot_answer_for_yields_no_gap()
    {
        // Same direction KycLimitWatchJob takes: a module that could not answer is "we do not
        // know", and the comparison must not turn that into a finding an officer has to clear.
        var databaseName = Guid.NewGuid().ToString();

        await Seed(
            databaseName,
            references: [(CustomerA, "EXT-001")],
            snapshots: [(CustomerA, KycLevel.Simplified)]);

        var kyc = Substitute.For<IKycModule>();
        kyc.GetLimitsAsync(Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .ThrowsAsyncForAnyArgs(new InvalidOperationException("M02 unavailable"));

        var report = await Run(
            databaseName,
            ReconciliationTestContext.NightOne,
            ReconciliationTestContext.Customers(ReconciliationTestContext.Client(CustomerA)),
            kyc);

        report.Checked.Should().Be(1);
        report.New.Should().Be(0);
    }

    [Fact]
    public async Task No_CbsKycMismatchDetectedEvent_is_published_by_the_reconciliation()
    {
        // Design decision 2 in one assertion. INT-21 is the per-customer notification published
        // at snapshot time; INT-34 is the ledger. If this job also published INT-21's event, the
        // same divergence would reach an administrator twice a day apart through two mechanisms
        // nobody could tell apart.
        var databaseName = Guid.NewGuid().ToString();

        await Seed(
            databaseName,
            references: [(CustomerA, "EXT-001")],
            snapshots: [(CustomerA, KycLevel.Simplified)]);

        await Run(
            databaseName,
            ReconciliationTestContext.NightOne,
            ReconciliationTestContext.Customers(ReconciliationTestContext.Client(CustomerA)),
            ReconciliationTestContext.Kyc("Full"));

        await using var db = ReconciliationTestContext.NewDb(
            ReconciliationTestContext.Tenant, databaseName);

        ReconciliationTestContext.Published<CbsKycMismatchDetectedEvent>(db).Should().BeEmpty();
        ReconciliationTestContext.Published<ReconciliationCompletedEvent>(db).Should().HaveCount(1);
    }

    // ── Helpers ─────────────────────────────────────────────────────────────

    private static async Task Seed(
        string databaseName,
        IReadOnlyCollection<(Guid CrmId, string ExternalId)> references,
        IReadOnlyCollection<(Guid CrmId, KycLevel? Level)> snapshots)
    {
        await using var db = ReconciliationTestContext.NewDb(
            ReconciliationTestContext.Tenant, databaseName);

        db.Connections.Add(ReconciliationTestContext.Connection(ReconciliationTestContext.Tenant));

        foreach (var (crmId, externalId) in references)
        {
            db.References.Add(ReconciliationTestContext.Reference(
                ReconciliationTestContext.Tenant, crmId, externalId));
        }

        foreach (var (crmId, level) in snapshots)
        {
            db.CbsSnapshots.Add(ReconciliationTestContext.Snapshot(
                ReconciliationTestContext.Tenant, crmId, level));
        }

        await db.SaveChangesAsync();
    }

    /// <summary>
    /// One night's comparison, over its own context — as a real run has, and as the second night
    /// of a dedup test needs so the change tracker of the first cannot flatter the result.
    /// </summary>
    private static async Task<ReconciliationReport> Run(
        string databaseName, DateTimeOffset at, ICustomersModule customers, IKycModule kyc)
    {
        await using var db = ReconciliationTestContext.NewDb(
            ReconciliationTestContext.Tenant, databaseName);

        return await ReconcileTenantJob.RunAsync(
            db,
            customers,
            kyc,
            ReconciliationTestContext.Publisher(db),
            new ReconciliationTestContext.FixedClock(at),
            NullLogger.Instance,
            ReconciliationTestContext.Tenant,
            CancellationToken.None);
    }

    private static async Task<List<IntegrationReconciliationGap>> AllGaps(string databaseName)
    {
        await using var db = ReconciliationTestContext.NewDb(
            ReconciliationTestContext.Tenant, databaseName);

        return await db.ReconciliationGaps.OrderBy(g => g.DetectedAt).ToListAsync();
    }

    private static async Task<IntegrationReconciliationGap> SingleGap(
        string databaseName, GapResolution resolution = GapResolution.Open)
    {
        var gaps = await AllGaps(databaseName);

        return gaps.Should().ContainSingle(g => g.Resolution == resolution).Subject;
    }
}
