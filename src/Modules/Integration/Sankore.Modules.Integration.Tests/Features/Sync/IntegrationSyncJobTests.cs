namespace Sankore.Modules.Integration.Tests.Features.Sync;

using System.Globalization;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Sankore.Modules.Integration.Domain;
using Sankore.Modules.Integration.Features.Snapshot;
using Sankore.Modules.Integration.Features.Sync;
using Sankore.Modules.Integration.Infrastructure;
using Xunit;

/// <summary>
/// INT-20 criteria 2, 3 and 5: the cursor advances only after the commit of the data it describes,
/// only referenced customers are synchronised, and a failing run neither propagates nor loses its
/// window.
/// </summary>
public sealed class IntegrationSyncJobTests
{
    private static readonly Guid TenantA = Guid.Parse("aaaaaaaa-3333-0000-0000-000000000001");
    private static readonly Guid TenantB = Guid.Parse("bbbbbbbb-4444-0000-0000-000000000002");

    [Fact]
    public async Task Advances_the_cursor_after_a_successful_run()
    {
        await using var db = SyncTestContext.NewDb(TenantA);

        var connection = SyncTestContext.SeedConnection(db, TenantA);
        var crmId = Guid.NewGuid();
        SyncTestContext.SeedReference(db, TenantA, connection.Id, crmId, "CBS-0001");

        var projector = new SyncTestContext.RecordingProjector();

        var report = await Run(db, projector, connection.Id);

        report.Projected.Should().Be(1);
        report.Failed.Should().BeFalse();

        var cursor = SyncTestContext.ReadCursor(db, TenantA, connection.Id, SyncStream.Customers);

        cursor.Should().NotBeNull();
        cursor!.Cursor.Should().NotBeNull();
        cursor.LastSuccessAt.Should().NotBeNull();
        cursor.ConsecutiveFailures.Should().Be(0);
        cursor.LastError.Should().BeNull();
    }

    [Fact]
    public async Task Creates_the_cursor_row_on_first_use()
    {
        // There is no seeder and there cannot usefully be one: a connection is created by an
        // administrator at any time, and a stream added to the enum would otherwise need a
        // backfill before it was ever swept.
        await using var db = SyncTestContext.NewDb(TenantA);

        var connection = SyncTestContext.SeedConnection(db, TenantA);

        SyncTestContext.ReadCursor(db, TenantA, connection.Id, SyncStream.Accounts)
            .Should().BeNull();

        await Run(db, new SyncTestContext.RecordingProjector(), connection.Id, SyncStream.Accounts);

        SyncTestContext.ReadCursor(db, TenantA, connection.Id, SyncStream.Accounts)
            .Should().NotBeNull();
    }

    [Fact]
    public async Task The_cursor_is_not_advanced_while_the_run_is_still_in_flight()
    {
        // Criterion 2, observed from inside. The projector reads the COMMITTED cursor through its
        // own context while the sweep is mid-walk: BeginRun has landed (so the orchestrator sees
        // the run and will not enqueue a second one) but Cursor is still null, because the data it
        // would describe has not been committed yet.
        var databaseName = Guid.NewGuid().ToString();

        await using var db = SyncTestContext.NewDb(TenantA, databaseName);

        var connection = SyncTestContext.SeedConnection(db, TenantA);
        SyncTestContext.SeedReference(db, TenantA, connection.Id, Guid.NewGuid(), "CBS-0001");

        var observations = new List<(DateTimeOffset? LastRunAt, string? Cursor)>();

        var projector = new SyncTestContext.RecordingProjector(onProject: _ =>
        {
            using var peek = SyncTestContext.NewDb(TenantA, databaseName);
            var row = SyncTestContext.ReadCursor(peek, TenantA, connection.Id, SyncStream.Customers);
            observations.Add((row?.LastRunAt, row?.Cursor));
            return Task.CompletedTask;
        });

        await Run(db, projector, connection.Id);

        observations.Should().ContainSingle();
        observations[0].LastRunAt.Should().NotBeNull("the attempt is visible before the work");
        observations[0].Cursor.Should().BeNull("the cursor may not move before the data commits");

        SyncTestContext.ReadCursor(db, TenantA, connection.Id, SyncStream.Customers)!
            .Cursor.Should().NotBeNull("and it does move once the data has committed");
    }

    [Fact]
    public async Task A_failure_between_the_work_and_the_commit_leaves_the_cursor_where_it_was()
    {
        // The criterion that loses data when done backwards. A run that fails must re-read the
        // same window next time rather than skip it, so the previous cursor value has to survive
        // untouched — and nothing of the half-done sweep may be committed either.
        await using var db = SyncTestContext.NewDb(TenantA);

        var connection = SyncTestContext.SeedConnection(db, TenantA);

        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        SyncTestContext.SeedReference(
            db, TenantA, connection.Id, first, "CBS-0001", createdAt: SyncTestContext.Now.AddDays(-2));
        SyncTestContext.SeedReference(
            db, TenantA, connection.Id, second, "CBS-0002", createdAt: SyncTestContext.Now.AddDays(-1));

        const string PreviousWindow = "2026-04-01T08:00:00.0000000+00:00";
        SyncTestContext.SeedCursor(
            db, TenantA, connection.Id, SyncStream.Customers,
            lastRunAt: SyncTestContext.Now.AddHours(-2), cursor: PreviousWindow);

        var projector = new SyncTestContext.RecordingProjector(throwOnCrmId: second);

        var report = await Run(db, projector, connection.Id);

        report.Failed.Should().BeTrue();
        report.Projected.Should().Be(1);

        var cursor = SyncTestContext.ReadCursor(db, TenantA, connection.Id, SyncStream.Customers);

        cursor!.Cursor.Should().Be(PreviousWindow, "the window must be re-read, not skipped");
        cursor.ConsecutiveFailures.Should().Be(1);
        cursor.LastError.Should().NotBeNullOrWhiteSpace();

        // Not even the customer that WAS projected is stamped: the data commit never happened, so
        // claiming any of them synchronised would be a second way of losing the window.
        var marked = await db.References
            .IgnoreQueryFilters()
            .AsNoTracking()
            .CountAsync(r => r.TenantId == TenantA && r.LastSyncedAt != null);

        marked.Should().Be(0);
    }

    [Fact]
    public async Task A_failing_run_does_not_throw_out_of_the_job()
    {
        // Criterion 5. Nothing propagates: the stream's other runs, the connection's other
        // streams and every other tenant's runs are separate Hangfire entries, and this one
        // failing must not be able to reach them.
        await using var db = SyncTestContext.NewDb(TenantA);

        var connection = SyncTestContext.SeedConnection(db, TenantA);
        var crmId = Guid.NewGuid();
        SyncTestContext.SeedReference(db, TenantA, connection.Id, crmId, "CBS-0001");

        var run = async () => await Run(
            db, new SyncTestContext.RecordingProjector(throwOnCrmId: crmId), connection.Id);

        await run.Should().NotThrowAsync();
    }

    [Fact]
    public async Task Records_the_failure_even_on_a_stream_that_never_had_a_cursor()
    {
        // Otherwise a stream that has failed every time since it was configured is
        // indistinguishable from one that has never been due.
        await using var db = SyncTestContext.NewDb(TenantA);

        var connection = SyncTestContext.SeedConnection(db, TenantA);
        var crmId = Guid.NewGuid();
        SyncTestContext.SeedReference(db, TenantA, connection.Id, crmId, "CBS-0001");

        await Run(db, new SyncTestContext.RecordingProjector(throwOnCrmId: crmId), connection.Id);

        var cursor = SyncTestContext.ReadCursor(db, TenantA, connection.Id, SyncStream.Customers);

        cursor.Should().NotBeNull();
        cursor!.Cursor.Should().BeNull();
        cursor.LastSuccessAt.Should().BeNull();
        cursor.ConsecutiveFailures.Should().Be(1);
    }

    [Fact]
    public async Task A_failing_connection_does_not_stop_the_other_connections_of_the_same_tenant()
    {
        await using var db = SyncTestContext.NewDb(TenantA);

        var failing = SyncTestContext.SeedConnection(db, TenantA, name: "CBS en panne");
        var healthy = SyncTestContext.SeedConnection(db, TenantA, name: "CBS sain");

        var failingCustomer = Guid.NewGuid();
        SyncTestContext.SeedReference(db, TenantA, failing.Id, failingCustomer, "CBS-0001");
        SyncTestContext.SeedReference(db, TenantA, healthy.Id, Guid.NewGuid(), "CBS-0002");

        var projector = new SyncTestContext.RecordingProjector(throwOnCrmId: failingCustomer);

        var failedReport = await Run(db, projector, failing.Id);
        var healthyReport = await Run(db, projector, healthy.Id);

        failedReport.Failed.Should().BeTrue();
        healthyReport.Failed.Should().BeFalse();
        healthyReport.Projected.Should().Be(1);

        SyncTestContext.ReadCursor(db, TenantA, failing.Id, SyncStream.Customers)!
            .Cursor.Should().BeNull();
        SyncTestContext.ReadCursor(db, TenantA, healthy.Id, SyncStream.Customers)!
            .Cursor.Should().NotBeNull();
    }

    [Fact]
    public async Task Synchronises_only_customers_present_in_integration_reference()
    {
        // Criterion 3, and it is not a performance filter. A reference row is written by INT-07 in
        // the same transaction as the write that created the customer on the other side, so it is
        // the only record that this tenant's CRM customer and that external identity are the same
        // person.
        await using var db = SyncTestContext.NewDb(TenantA);

        var connection = SyncTestContext.SeedConnection(db, TenantA);
        var otherConnection = SyncTestContext.SeedConnection(db, TenantA, name: "Autre CBS");

        var mine = Guid.NewGuid();
        var alsoMine = Guid.NewGuid();

        SyncTestContext.SeedReference(
            db, TenantA, connection.Id, mine, "CBS-0001",
            createdAt: SyncTestContext.Now.AddDays(-3));
        SyncTestContext.SeedReference(
            db, TenantA, connection.Id, alsoMine, "CBS-0002",
            createdAt: SyncTestContext.Now.AddDays(-2));

        // An account of the same connection: a reference, but not a customer.
        SyncTestContext.SeedReference(
            db, TenantA, connection.Id, Guid.NewGuid(), "ACC-0001",
            entityType: IntegrationEntityTypes.Account);

        // A customer of ANOTHER connection of the same tenant: known, but not through this system.
        SyncTestContext.SeedReference(
            db, TenantA, otherConnection.Id, Guid.NewGuid(), "CBS-0003");

        // And another tenant's customer, on an id that would collide if the predicate forgot its
        // explicit tenant half.
        SyncTestContext.SeedReference(db, TenantB, connection.Id, Guid.NewGuid(), "CBS-0004");

        var projector = new SyncTestContext.RecordingProjector();

        await Run(db, projector, connection.Id);

        projector.Projected.Select(p => p.CrmCustomerId).Should().BeEquivalentTo([mine, alsoMine]);
        projector.Projected.Should().OnlyContain(
            p => p.TenantId == TenantA && p.ConnectionId == connection.Id);
    }

    [Fact]
    public async Task Stamps_every_synchronised_reference()
    {
        await using var db = SyncTestContext.NewDb(TenantA);

        var connection = SyncTestContext.SeedConnection(db, TenantA);
        SyncTestContext.SeedReference(db, TenantA, connection.Id, Guid.NewGuid(), "CBS-0001");

        await Run(db, new SyncTestContext.RecordingProjector(), connection.Id);

        var reference = await db.References
            .IgnoreQueryFilters()
            .AsNoTracking()
            .SingleAsync(r => r.TenantId == TenantA);

        reference.LastSyncedAt.Should().NotBeNull();
    }

    [Fact]
    public async Task The_cursor_is_advanced_to_the_instant_the_run_started()
    {
        // Not to the instant it finished. Anything that changed in the external system while the
        // run was walking must be re-read by the next one; the end instant would step over exactly
        // those records. The stepping clock is what makes the two distinguishable at all.
        await using var db = SyncTestContext.NewDb(TenantA);

        var connection = SyncTestContext.SeedConnection(db, TenantA);
        SyncTestContext.SeedReference(db, TenantA, connection.Id, Guid.NewGuid(), "CBS-0001");

        var clock = new SyncTestContext.SteppingClock(SyncTestContext.Now);

        await IntegrationSyncJob.RunAsync(
            db, new SyncTestContext.RecordingProjector(), clock,
            NullLogger<IntegrationSyncJobTests>.Instance,
            TenantA, connection.Id, SyncStream.Customers, CancellationToken.None);

        var cursor = SyncTestContext.ReadCursor(db, TenantA, connection.Id, SyncStream.Customers);

        cursor!.Cursor.Should().Be(clock.First.ToString("O", CultureInfo.InvariantCulture));
        cursor.LastSuccessAt.Should().BeAfter(clock.First);
    }

    [Fact]
    public async Task Skips_a_connection_deactivated_between_the_fan_out_and_the_run()
    {
        // Criterion 1 scopes the sweep to ACTIVE connections, so the check belongs at the point of
        // work too — and the cursor is left completely alone, because a run that did not happen
        // must not look like one that did.
        await using var db = SyncTestContext.NewDb(TenantA);

        var connection = SyncTestContext.SeedConnection(db, TenantA, active: false);
        SyncTestContext.SeedReference(db, TenantA, connection.Id, Guid.NewGuid(), "CBS-0001");

        var projector = new SyncTestContext.RecordingProjector();

        var report = await Run(db, projector, connection.Id);

        report.Skipped.Should().BeTrue();
        report.Projected.Should().Be(0);
        projector.Projected.Should().BeEmpty();
        SyncTestContext.ReadCursor(db, TenantA, connection.Id, SyncStream.Customers).Should().BeNull();
    }

    private static Task<SyncRunReport> Run(
        IntegrationDbContext db,
        ICbsSnapshotProjector projector,
        Guid connectionId,
        SyncStream stream = SyncStream.Customers,
        Guid? tenantId = null)
        => IntegrationSyncJob.RunAsync(
            db,
            projector,
            new SyncTestContext.FixedClock(SyncTestContext.Now),
            NullLogger<IntegrationSyncJobTests>.Instance,
            tenantId ?? TenantA,
            connectionId,
            stream,
            CancellationToken.None);
}
