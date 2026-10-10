namespace Sankore.Modules.Integration.Tests.Features.Reconciliation;

using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Sankore.Modules.Customers.PublicApi;
using Sankore.Modules.Integration.Domain;
using Sankore.Modules.Integration.Features.Reconciliation;
using Sankore.Modules.Integration.PublicApi;
using Sankore.Modules.Kyc.PublicApi;
using Xunit;

/// <summary>
/// The insurance half of the nightly comparison, end to end (BNF-B-17): policy references against
/// the insurer's record and against what was actually sent, writing into the SAME ledger the
/// customer comparison uses.
/// </summary>
public sealed class ReconcileHoldingsTests
{
    private static readonly Guid Insurance = new("11111111-0000-0000-0000-000000000011");
    private static readonly Guid HoldingA = new("22222222-0000-0000-0000-000000000022");
    private static readonly Guid HoldingB = new("33333333-0000-0000-0000-000000000033");

    // ── fixtures ────────────────────────────────────────────────────────────

    /// <summary>
    /// Seeds one insurance connection with one policy reference, and whatever the scenario needs
    /// on the external and command sides.
    /// </summary>
    private static async Task<string> SeedAsync(
        bool withPolicy = true,
        bool policyLive = true,
        int sentClauseVersion = 1,
        Guid? holdingId = null)
    {
        var databaseName = Guid.NewGuid().ToString();
        var holding = holdingId ?? HoldingA;

        await using var db = ReconciliationTestContext.NewDb(
            ReconciliationTestContext.Tenant, databaseName);

        db.Connections.Add(ReconciliationTestContext.InsuranceConnection(
            ReconciliationTestContext.Tenant, Insurance));

        db.References.Add(ReconciliationTestContext.PolicyReference(
            ReconciliationTestContext.Tenant, holding, "POL-00042", Insurance));

        if (withPolicy)
        {
            db.Policies.Add(ReconciliationTestContext.Policy(
                ReconciliationTestContext.Tenant, Insurance, "POL-00042", policyLive));
        }

        if (sentClauseVersion > 0)
        {
            db.Commands.Add(ReconciliationTestContext.SentClause(
                ReconciliationTestContext.Tenant, Insurance, holding, sentClauseVersion));
        }

        await db.SaveChangesAsync();
        return databaseName;
    }

    private static async Task<ReconciliationReport> RunAsync(
        string databaseName, ICustomersModule customers, DateTimeOffset? at = null)
    {
        await using var db = ReconciliationTestContext.NewDb(
            ReconciliationTestContext.Tenant, databaseName);

        return await ReconcileTenantJob.RunAsync(
            db,
            customers,
            // M02 is never asked on this path — a KYC tier is not a question about a policy.
            Substitute.For<IKycModule>(),
            ReconciliationTestContext.Publisher(db),
            new ReconciliationTestContext.FixedClock(at ?? ReconciliationTestContext.NightOne),
            NullLogger.Instance,
            ReconciliationTestContext.Tenant,
            CancellationToken.None);
    }

    private static async Task<List<IntegrationReconciliationGap>> GapsAsync(string databaseName)
    {
        await using var db = ReconciliationTestContext.NewDb(
            ReconciliationTestContext.Tenant, databaseName);

        return await db.ReconciliationGaps.IgnoreQueryFilters().ToListAsync();
    }

    // ── the two sides agree ─────────────────────────────────────────────────

    [Fact]
    public async Task A_live_holding_the_insurer_confirms_opens_no_gap()
    {
        var databaseName = await SeedAsync();

        var report = await RunAsync(
            databaseName,
            ReconciliationTestContext.Holdings(
                ReconciliationTestContext.HoldingState(HoldingA)));

        report.Connections.Should().Be(1);
        report.Checked.Should().Be(1);
        report.Observed.Should().Be(0);

        (await GapsAsync(databaseName)).Should().BeEmpty();
    }

    // ── the three findings ──────────────────────────────────────────────────

    [Fact]
    public async Task A_subscription_the_insurer_never_confirmed_is_reported()
    {
        var databaseName = await SeedAsync(withPolicy: false);

        var report = await RunAsync(
            databaseName,
            ReconciliationTestContext.Holdings(
                ReconciliationTestContext.HoldingState(HoldingA)));

        report.New.Should().Be(1);

        var gap = (await GapsAsync(databaseName)).Should().ContainSingle().Subject;
        gap.GapType.Should().Be(GapType.MissingInExternal);
        gap.CrmId.Should().Be(HoldingA, "the gap points at the HOLDING, not at the customer");
        gap.ExternalId.Should().BeNull("MissingInExternal carries no external id, per the aggregate");
        gap.ConnectionId.Should().Be(Insurance);
    }

    [Fact]
    public async Task A_policy_the_insurer_has_cancelled_is_reported()
    {
        var databaseName = await SeedAsync(policyLive: false);

        await RunAsync(
            databaseName,
            ReconciliationTestContext.Holdings(
                ReconciliationTestContext.HoldingState(HoldingA)));

        var gap = (await GapsAsync(databaseName)).Should().ContainSingle().Subject;
        gap.GapType.Should().Be(GapType.StatusMismatch);
        gap.ExternalId.Should().Be("POL-00042", "a status mismatch names both sides");
    }

    [Fact]
    public async Task An_avenant_that_never_reached_the_insurer_is_reported()
    {
        // The CRM is at clause 2; only clause 1 was ever sent. The policy would pay the previous
        // beneficiaries, which is precisely the finding BNF-B-17 adds.
        var databaseName = await SeedAsync(sentClauseVersion: 1);

        await RunAsync(
            databaseName,
            ReconciliationTestContext.Holdings(
                ReconciliationTestContext.HoldingState(HoldingA, clauseVersion: 2)));

        var gap = (await GapsAsync(databaseName)).Should().ContainSingle().Subject;
        gap.GapType.Should().Be(GapType.DesignationOutOfSync);
        gap.DetailsJson.Should().Contain("\"crmClauseVersion\":2");
    }

    [Fact]
    public async Task An_avenant_that_DID_reach_the_insurer_is_clean()
    {
        // The contrast, so the test above is not passing because the lookup never matches: the
        // key is recomputed by the production factory on both sides.
        var databaseName = await SeedAsync(sentClauseVersion: 2);

        await RunAsync(
            databaseName,
            ReconciliationTestContext.Holdings(
                ReconciliationTestContext.HoldingState(HoldingA, clauseVersion: 2)));

        (await GapsAsync(databaseName)).Should().BeEmpty();
    }

    [Fact]
    public async Task A_rejected_intermediate_avenant_does_not_produce_a_false_finding()
    {
        // v1 and v3 landed, v2 was rejected. Counting succeeded commands would say "2 sent" and
        // report a gap on a contract that is in fact up to date; the EXACT key lookup does not.
        var databaseName = Guid.NewGuid().ToString();

        await using (var db = ReconciliationTestContext.NewDb(
            ReconciliationTestContext.Tenant, databaseName))
        {
            db.Connections.Add(ReconciliationTestContext.InsuranceConnection(
                ReconciliationTestContext.Tenant, Insurance));

            db.References.Add(ReconciliationTestContext.PolicyReference(
                ReconciliationTestContext.Tenant, HoldingA, "POL-00042", Insurance));

            db.Policies.Add(ReconciliationTestContext.Policy(
                ReconciliationTestContext.Tenant, Insurance, "POL-00042"));

            db.Commands.Add(ReconciliationTestContext.SentClause(
                ReconciliationTestContext.Tenant, Insurance, HoldingA, 1));

            db.Commands.Add(ReconciliationTestContext.SentClause(
                ReconciliationTestContext.Tenant, Insurance, HoldingA, 3));

            await db.SaveChangesAsync();
        }

        await RunAsync(
            databaseName,
            ReconciliationTestContext.Holdings(
                ReconciliationTestContext.HoldingState(HoldingA, clauseVersion: 3)));

        (await GapsAsync(databaseName)).Should().BeEmpty(
            "clause 3 landed, whatever happened to clause 2");
    }

    // ── the ledger behaves the same as the customer side ────────────────────

    [Fact]
    public async Task A_recurring_gap_keeps_its_row_and_its_detection_date()
    {
        var databaseName = await SeedAsync(withPolicy: false);
        var customers = ReconciliationTestContext.Holdings(
            ReconciliationTestContext.HoldingState(HoldingA));

        await RunAsync(databaseName, customers, ReconciliationTestContext.NightOne);
        var second = await RunAsync(databaseName, customers, ReconciliationTestContext.NightTwo);

        second.New.Should().Be(0);
        second.Recurring.Should().Be(1);

        var gap = (await GapsAsync(databaseName)).Should().ContainSingle().Subject;
        gap.DetectedAt.Should().Be(ReconciliationTestContext.NightOne,
            "the age of the problem is the figure an inspection asks for");
        gap.LastSeenAt.Should().Be(ReconciliationTestContext.NightTwo);
    }

    [Fact]
    public async Task A_gap_that_stopped_being_true_closes_itself()
    {
        var databaseName = await SeedAsync(withPolicy: false);

        await RunAsync(
            databaseName,
            ReconciliationTestContext.Holdings(
                ReconciliationTestContext.HoldingState(HoldingA)),
            ReconciliationTestContext.NightOne);

        // The insurer confirms it overnight.
        await using (var db = ReconciliationTestContext.NewDb(
            ReconciliationTestContext.Tenant, databaseName))
        {
            db.Policies.Add(ReconciliationTestContext.Policy(
                ReconciliationTestContext.Tenant, Insurance, "POL-00042"));

            await db.SaveChangesAsync();
        }

        var second = await RunAsync(
            databaseName,
            ReconciliationTestContext.Holdings(
                ReconciliationTestContext.HoldingState(HoldingA)),
            ReconciliationTestContext.NightTwo);

        second.Closed.Should().Be(1);

        var gap = (await GapsAsync(databaseName)).Should().ContainSingle().Subject;
        gap.Resolution.Should().Be(GapResolution.Closed);
        gap.ResolvedBy.Should().BeNull("nobody decided it — it stopped being true");
    }

    [Fact]
    public async Task A_reference_whose_holding_M01_does_not_know_opens_no_gap()
    {
        var databaseName = await SeedAsync(withPolicy: false);

        // M01 knows a DIFFERENT holding, so the referenced one is absent from the answer.
        var report = await RunAsync(
            databaseName,
            ReconciliationTestContext.Holdings(
                ReconciliationTestContext.HoldingState(HoldingB)));

        report.UnknownInCrm.Should().Be(1);
        (await GapsAsync(databaseName)).Should().BeEmpty(
            "M01 deletes no holding, so this is a data-integrity anomaly rather than a finding");
    }

    [Fact]
    public async Task The_run_row_declares_what_an_insurance_run_cannot_look_for()
    {
        var databaseName = await SeedAsync();

        await RunAsync(
            databaseName,
            ReconciliationTestContext.Holdings(
                ReconciliationTestContext.HoldingState(HoldingA)));

        await using var db = ReconciliationTestContext.NewDb(
            ReconciliationTestContext.Tenant, databaseName);

        var run = (await db.ReconciliationRuns.IgnoreQueryFilters().ToListAsync())
            .Should().ContainSingle().Subject;

        run.UndetectableGapTypes.Should().Be(
            $"{nameof(GapType.MissingInCrm)},{nameof(GapType.KycMismatch)}",
            "a KYC tier is not a question about a policy, and the insurer's portfolio cannot be "
            + "enumerated — a type that can never fire must not read as one that found nothing");
    }

    [Fact]
    public async Task A_summary_is_published_only_when_something_is_new()
    {
        var databaseName = await SeedAsync(withPolicy: false);
        var customers = ReconciliationTestContext.Holdings(
            ReconciliationTestContext.HoldingState(HoldingA));

        await RunAsync(databaseName, customers, ReconciliationTestContext.NightOne);
        await RunAsync(databaseName, customers, ReconciliationTestContext.NightTwo);

        await using var db = ReconciliationTestContext.NewDb(
            ReconciliationTestContext.Tenant, databaseName);

        var announcements = await db.OutboxMessages
            .Where(m => m.EventType.Contains(nameof(ReconciliationCompletedEvent)))
            .ToListAsync();

        announcements.Should().HaveCount(1,
            "a still-open gap already has an owner; a nightly mail regardless is one nobody opens");
    }
}
