namespace Sankore.Modules.Integration.Tests.Features.Batch.Inbound;

using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Sankore.Modules.Integration.Domain;
using Sankore.Modules.Integration.Features.Batch.Inbound;
using Sankore.Modules.Integration.Infrastructure;
using Xunit;

/// <summary>
/// INT-25 criterion 3: a <c>Batched</c> command with no acknowledgement beyond the connection's
/// configurable delay raises an alert — <b>exactly one, and only one however often the sweep
/// runs</b>.
/// </summary>
public sealed class AckOverdueSweepTests
{
    private static readonly Guid FileId = new("f0000000-0000-0000-0000-00000000000e");

    [Fact]
    public async Task A_batched_command_past_the_delay_raises_exactly_one_alert()
    {
        var db = Seed(sentHoursAgo: 50, ackTimeoutHours: 48, commandCount: 3, out var connection, out _);
        var alerter = new InboundBatchTestContext.RecordingAlerter();

        var raised = await Run(db, alerter, connection);

        raised.Should().Be(1);
        alerter.Alerts.Should().HaveCount(1);

        var alert = alerter.Alerts[0];
        alert.TenantId.Should().Be(InboundBatchTestContext.TenantA);
        alert.ConnectionId.Should().Be(InboundBatchTestContext.ConnectionA);
        alert.BatchFileId.Should().Be(FileId);
        alert.AckTimeoutHours.Should().Be(48);

        // Per file, carrying the count: three commands are one event, not three notifications.
        alert.PendingCommandCount.Should().Be(3);
    }

    [Fact]
    public async Task Two_runs_raise_the_alert_only_once()
    {
        var db = Seed(sentHoursAgo: 50, ackTimeoutHours: 48, commandCount: 2, out var connection, out var name);
        var alerter = new InboundBatchTestContext.RecordingAlerter();

        await Run(db, alerter, connection);
        await Run(db, alerter, connection);

        alerter.Alerts.Should().HaveCount(1);

        await using var fresh = InboundBatchTestContext.NewDb(InboundBatchTestContext.TenantA, name);

        // The ledger is the file's own status: Sent → Failed, so IsAckOverdue is false for ever
        // after. FailureDetail is what an operator reads in the batch list.
        var file = await fresh.BatchFiles.SingleAsync(f => f.Id == FileId);
        file.Status.Should().Be(BatchFileStatus.Failed);
        file.FailureDetail.Should().Contain(InboundBatchCodes.AckOverdue).And.Contain("2 command");
    }

    [Fact]
    public async Task The_commands_are_left_batched_so_a_late_acknowledgement_still_closes_them()
    {
        var db = Seed(sentHoursAgo: 50, ackTimeoutHours: 48, commandCount: 1, out var connection, out var name);

        await Run(db, new InboundBatchTestContext.RecordingAlerter(), connection);

        await using var fresh = InboundBatchTestContext.NewDb(InboundBatchTestContext.TenantA, name);

        // Rejecting them would be deciding an outcome the external system never stated, and a
        // command closed wrongly cannot be un-closed.
        (await fresh.Commands.SingleAsync()).Status.Should().Be(CommandStatus.Batched);
    }

    [Fact]
    public async Task A_file_within_its_delay_raises_nothing()
    {
        var db = Seed(sentHoursAgo: 10, ackTimeoutHours: 48, commandCount: 1, out var connection, out var name);
        var alerter = new InboundBatchTestContext.RecordingAlerter();

        (await Run(db, alerter, connection)).Should().Be(0);
        alerter.Alerts.Should().BeEmpty();

        await using var fresh = InboundBatchTestContext.NewDb(InboundBatchTestContext.TenantA, name);
        (await fresh.BatchFiles.SingleAsync()).Status.Should().Be(BatchFileStatus.Sent);
    }

    [Fact]
    public async Task The_delay_is_the_connections_own()
    {
        // Twelve hours old. Unremarkable at 48 h, overdue at 6 h — which is the point of the
        // setting living on the connection rather than being a platform constant.
        var patient = Seed(12, ackTimeoutHours: 48, commandCount: 1, out var patientConn, out _);
        var impatient = Seed(12, ackTimeoutHours: 6, commandCount: 1, out var impatientConn, out _);

        (await Run(patient, new InboundBatchTestContext.RecordingAlerter(), patientConn))
            .Should().Be(0);

        (await Run(impatient, new InboundBatchTestContext.RecordingAlerter(), impatientConn))
            .Should().Be(1);
    }

    [Fact]
    public async Task A_file_whose_commands_all_closed_is_left_exactly_as_it_is()
    {
        var db = Seed(sentHoursAgo: 50, ackTimeoutHours: 48, commandCount: 0, out var connection, out var name);
        var alerter = new InboundBatchTestContext.RecordingAlerter();

        (await Run(db, alerter, connection)).Should().Be(0);
        alerter.Alerts.Should().BeEmpty();

        await using var fresh = InboundBatchTestContext.NewDb(InboundBatchTestContext.TenantA, name);

        // Criterion 3 is about a command left waiting, and there is none: what becomes of an
        // unanswered file whose work is nevertheless done is INT-24's business, not an alert
        // sweep's.
        (await fresh.BatchFiles.SingleAsync()).Status.Should().Be(BatchFileStatus.Sent);
    }

    [Fact]
    public async Task An_inbound_file_is_never_alerted_on()
    {
        var name = InboundBatchTestContext.NewDbName();
        var db = InboundBatchTestContext.NewDb(InboundBatchTestContext.TenantA, name);
        var connection = InboundBatchTestContext.Connection(
            InboundBatchTestContext.TenantA, InboundBatchTestContext.ConnectionA);

        db.BatchFiles.Add(InboundBatchTestContext.RecordedInboundFile(
            InboundBatchTestContext.TenantA, InboundBatchTestContext.ConnectionA, 1,
            BatchFileStatus.Processed));

        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var alerter = new InboundBatchTestContext.RecordingAlerter();

        (await Run(db, alerter, connection)).Should().Be(0);
    }

    [Fact]
    public async Task Another_tenants_overdue_file_is_never_alerted_on()
    {
        var name = InboundBatchTestContext.NewDbName();

        await using (var seed = InboundBatchTestContext.NewDb(InboundBatchTestContext.TenantB, name))
        {
            seed.BatchFiles.Add(InboundBatchTestContext.SentOutboundFile(
                InboundBatchTestContext.TenantB, InboundBatchTestContext.ConnectionA, FileId,
                sentHoursAgo: 90));

            seed.Commands.Add(InboundBatchTestContext.BatchedCommand(
                InboundBatchTestContext.TenantB, InboundBatchTestContext.ConnectionA, FileId));

            await seed.SaveChangesAsync();
        }

        var db = InboundBatchTestContext.NewDb(InboundBatchTestContext.TenantA, name);
        var connection = InboundBatchTestContext.Connection(
            InboundBatchTestContext.TenantA, InboundBatchTestContext.ConnectionA);

        var alerter = new InboundBatchTestContext.RecordingAlerter();

        (await Run(db, alerter, connection)).Should().Be(0);
        alerter.Alerts.Should().BeEmpty();

        await using var fresh = InboundBatchTestContext.NewDb(InboundBatchTestContext.TenantB, name);
        (await fresh.BatchFiles.SingleAsync()).Status.Should().Be(BatchFileStatus.Sent);
    }

    [Fact]
    public async Task Only_commands_of_the_file_being_alerted_on_are_counted()
    {
        var db = Seed(sentHoursAgo: 50, ackTimeoutHours: 48, commandCount: 1, out var connection, out _);

        // A command of a DIFFERENT file of the same connection, also Batched.
        db.Commands.Add(InboundBatchTestContext.BatchedCommand(
            InboundBatchTestContext.TenantA, InboundBatchTestContext.ConnectionA,
            batchFileId: Guid.NewGuid()));

        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var alerter = new InboundBatchTestContext.RecordingAlerter();
        await Run(db, alerter, connection);

        alerter.Alerts.Single().PendingCommandCount.Should().Be(1);
    }

    // ── helpers ─────────────────────────────────────────────────────────────

    private static Task<int> Run(
        IntegrationDbContext db,
        IBatchAckOverdueAlerter alerter,
        IntegrationConnection connection)
        => AckOverdueSweep.RunAsync(
            db, alerter, new InboundBatchTestContext.FixedClock(InboundBatchTestContext.Now),
            NullLogger.Instance, InboundBatchTestContext.TenantA, connection,
            CancellationToken.None);

    private static IntegrationDbContext Seed(
        int sentHoursAgo,
        int ackTimeoutHours,
        int commandCount,
        out IntegrationConnection connection,
        out string databaseName)
    {
        databaseName = InboundBatchTestContext.NewDbName();

        connection = InboundBatchTestContext.Connection(
            InboundBatchTestContext.TenantA, InboundBatchTestContext.ConnectionA,
            ackTimeoutHours: ackTimeoutHours);

        var db = InboundBatchTestContext.NewDb(InboundBatchTestContext.TenantA, databaseName);

        db.Connections.Add(connection);
        db.BatchFiles.Add(InboundBatchTestContext.SentOutboundFile(
            InboundBatchTestContext.TenantA, InboundBatchTestContext.ConnectionA, FileId,
            sentHoursAgo: sentHoursAgo));

        for (var i = 0; i < commandCount; i++)
            db.Commands.Add(InboundBatchTestContext.BatchedCommand(
                InboundBatchTestContext.TenantA, InboundBatchTestContext.ConnectionA, FileId));

        db.SaveChanges();
        db.ChangeTracker.Clear();

        return db;
    }
}
