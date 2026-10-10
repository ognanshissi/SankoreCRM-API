namespace Sankore.Modules.Integration.Tests.Features.Reconciliation;

using FluentAssertions;
using Sankore.Modules.Integration.Domain;
using Sankore.Modules.Integration.Features.Reconciliation.ListGaps;
using Sankore.Modules.Integration.Infrastructure;
using Sankore.Modules.Integration.PublicApi;
using Xunit;

/// <summary>
/// The ledger read behind <c>Integration.Reconciliation.View</c> — the permission that sat in
/// <c>Permissions.All</c> with nothing behind it.
/// </summary>
public sealed class ListReconciliationGapsHandlerTests
{
    [Fact]
    public async Task Returns_the_open_gaps_oldest_first()
    {
        var databaseName = Guid.NewGuid().ToString();

        await using var db = ReconciliationTestContext.NewDb(
            ReconciliationTestContext.Tenant, databaseName);

        Add(db, GapType.MissingInExternal, ReconciliationTestContext.NightTwo);
        Add(db, GapType.KycMismatch, ReconciliationTestContext.NightOne);
        await db.SaveChangesAsync();

        var page = await Handle(db, new ListReconciliationGapsQuery(null, null, null, 1, 20));

        // Oldest first: the figure that matters about a finding is how long it has been open, and
        // the row at the top is the one an inspection will ask about.
        page.Rows.Select(r => r.GapType).Should().ContainInOrder(
            nameof(GapType.KycMismatch), nameof(GapType.MissingInExternal));

        page.TotalCount.Should().Be(2);
    }

    [Fact]
    public async Task Hides_resolved_and_closed_gaps_unless_they_are_asked_for()
    {
        var databaseName = Guid.NewGuid().ToString();

        await using var db = ReconciliationTestContext.NewDb(
            ReconciliationTestContext.Tenant, databaseName);

        Add(db, GapType.MissingInExternal, ReconciliationTestContext.NightOne);

        var closed = Add(db, GapType.KycMismatch, ReconciliationTestContext.NightOne);
        closed.CloseAutomatically(
            new ReconciliationTestContext.FixedClock(ReconciliationTestContext.NightTwo));

        await db.SaveChangesAsync();

        var open = await Handle(db, new ListReconciliationGapsQuery(null, null, null, 1, 20));
        open.Rows.Should().ContainSingle()
            .Which.GapType.Should().Be(nameof(GapType.MissingInExternal));

        var history = await Handle(db, new ListReconciliationGapsQuery(null, null, "Closed", 1, 20));
        history.Rows.Should().ContainSingle()
            .Which.GapType.Should().Be(nameof(GapType.KycMismatch));
    }

    [Fact]
    public async Task The_open_counts_cover_every_detectable_type_and_do_not_follow_the_page()
    {
        var databaseName = Guid.NewGuid().ToString();

        await using var db = ReconciliationTestContext.NewDb(
            ReconciliationTestContext.Tenant, databaseName);

        Add(db, GapType.MissingInExternal, ReconciliationTestContext.NightOne);
        Add(db, GapType.MissingInExternal, ReconciliationTestContext.NightOne);
        Add(db, GapType.StatusMismatch, ReconciliationTestContext.NightOne);
        await db.SaveChangesAsync();

        // One row per page: the headline counts must not change when somebody turns a page.
        var page = await Handle(db, new ListReconciliationGapsQuery(null, null, null, 1, 1));

        page.Rows.Should().HaveCount(1);

        page.OpenCountsByType.Should().BeEquivalentTo(new Dictionary<string, int>
        {
            // A clean type is a ZERO and not a missing key: a reader must be able to tell
            // "looked for, none found" from "not in the answer".
            [nameof(GapType.MissingInExternal)] = 2,
            [nameof(GapType.StatusMismatch)] = 1,
            [nameof(GapType.KycMismatch)] = 0,
        });

        // And the type nothing can feed is NOT seeded with a zero among them — a zero against it
        // would read as a measurement. It travels separately, named.
        page.OpenCountsByType.Should().NotContainKey(nameof(GapType.MissingInCrm));
        page.UndetectableGapTypes.Should().BeEquivalentTo([nameof(GapType.MissingInCrm)]);
    }

    [Fact]
    public async Task Surfaces_the_last_run_so_an_empty_ledger_can_be_told_from_a_failed_comparison()
    {
        // An empty page under a run that CRASHED last night means something very different from
        // an empty page under a run that finished. Without this the two are indistinguishable,
        // and the reassuring reading is the wrong one.
        var databaseName = Guid.NewGuid().ToString();

        await using var db = ReconciliationTestContext.NewDb(
            ReconciliationTestContext.Tenant, databaseName);

        var run = IntegrationReconciliationRun.Start(
            ReconciliationTestContext.Tenant,
            ReconciliationTestContext.ConnectionId,
            new ReconciliationTestContext.FixedClock(ReconciliationTestContext.NightOne),
            [GapType.MissingInCrm]);

        run.Fail("M01 unavailable.",
            new ReconciliationTestContext.FixedClock(ReconciliationTestContext.NightOne));

        db.ReconciliationRuns.Add(run);
        await db.SaveChangesAsync();

        var page = await Handle(db, new ListReconciliationGapsQuery(null, null, null, 1, 20));

        page.Rows.Should().BeEmpty();
        page.LastRun!.FailureDetail.Should().Be("M01 unavailable.");
        page.LastRun.FinishedAt.Should().Be(ReconciliationTestContext.NightOne);
    }

    [Fact]
    public async Task Another_tenants_gaps_are_absent_rather_than_refused()
    {
        var databaseName = Guid.NewGuid().ToString();

        await using (var other = ReconciliationTestContext.NewDb(
            ReconciliationTestContext.OtherTenant, databaseName))
        {
            Add(other, GapType.MissingInExternal, ReconciliationTestContext.NightOne,
                ReconciliationTestContext.OtherTenant);
            await other.SaveChangesAsync();
        }

        await using var db = ReconciliationTestContext.NewDb(
            ReconciliationTestContext.Tenant, databaseName);

        var page = await Handle(db, new ListReconciliationGapsQuery(null, null, null, 1, 20));

        page.Rows.Should().BeEmpty();
        page.TotalCount.Should().Be(0);
        page.OpenCountsByType.Values.Should().AllSatisfy(c => c.Should().Be(0));
    }

    [Fact]
    public async Task An_unparsable_filter_is_refused_rather_than_ignored()
    {
        // Dropping the filter silently would answer with every gap ever recorded, which reads as
        // a catastrophic morning, or with the whole ledger when the caller asked for one type.
        await using var db = ReconciliationTestContext.NewDb(
            ReconciliationTestContext.Tenant, Guid.NewGuid().ToString());

        var badResolution = await new ListReconciliationGapsHandler(db).Handle(
            new ListReconciliationGapsQuery(null, null, "Pending", 1, 20), CancellationToken.None);

        var badType = await new ListReconciliationGapsHandler(db).Handle(
            new ListReconciliationGapsQuery(null, "NotAType", null, 1, 20), CancellationToken.None);

        badResolution.IsFailure.Should().BeTrue();
        badResolution.Error.Should().Be(IntegrationErrors.PayloadInvalid);
        badType.IsFailure.Should().BeTrue();
    }

    [Fact]
    public async Task A_page_index_below_one_is_clamped_rather_than_throwing()
    {
        // Skip() with a negative count throws; a front-end off-by-one must not be a 500.
        await using var db = ReconciliationTestContext.NewDb(
            ReconciliationTestContext.Tenant, Guid.NewGuid().ToString());

        Add(db, GapType.MissingInExternal, ReconciliationTestContext.NightOne);
        await db.SaveChangesAsync();

        var page = await Handle(db, new ListReconciliationGapsQuery(null, null, null, 0, 0));

        page.Page.Should().Be(1);
        page.PageSize.Should().BeGreaterThan(0);
        page.Rows.Should().HaveCount(1);
    }

    private static async Task<ReconciliationGapPage> Handle(
        IntegrationDbContext db, ListReconciliationGapsQuery query)
    {
        var result = await new ListReconciliationGapsHandler(db)
            .Handle(query, CancellationToken.None);

        result.IsSuccess.Should().BeTrue(result.Error);

        return result.Value;
    }

    private static IntegrationReconciliationGap Add(
        IntegrationDbContext db, GapType type, DateTimeOffset at, Guid? tenantId = null)
    {
        var gap = IntegrationReconciliationGap.Open(
            tenantId: tenantId ?? ReconciliationTestContext.Tenant,
            runId: Guid.NewGuid(),
            connectionId: ReconciliationTestContext.ConnectionId,
            gapType: type,
            crmId: Guid.NewGuid(),
            externalId: type == GapType.MissingInExternal ? null : $"EXT-{Guid.NewGuid():N}",
            clock: new ReconciliationTestContext.FixedClock(at));

        db.ReconciliationGaps.Add(gap);

        return gap;
    }
}
