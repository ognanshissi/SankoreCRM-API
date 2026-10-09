namespace Sankore.Modules.Integration.Tests.Features.Batch.Inbound;

using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Sankore.Modules.Integration.Domain;
using Sankore.Modules.Integration.Features.Batch.Inbound;
using Sankore.Modules.Integration.Features.Snapshot;
using Sankore.Modules.Integration.Infrastructure;
using Sankore.Modules.Integration.PublicApi;
using Xunit;

/// <summary>
/// INT-25 criterion 1 (poll, verify the checksum and the sequence, ignore what is already
/// processed) and the two appliers seen through the job: criterion 2 end to end, and criterion 4's
/// call into INT-21's projector.
/// </summary>
public sealed class PollInboundBatchFilesJobTests
{
    private const string AckName = "SNK-ACK-000012.csv";
    private const string ExtractName = "SNK-EXT-000012.csv";

    private static readonly Guid CommandOne = new("10000000-0000-0000-0000-000000000001");
    private static readonly Guid CommandTwo = new("10000000-0000-0000-0000-000000000002");
    private static readonly Guid CrmOne = new("20000000-0000-0000-0000-000000000001");
    private static readonly Guid CrmTwo = new("20000000-0000-0000-0000-000000000002");

    [Fact]
    public async Task A_valid_ack_file_closes_its_commands_writes_the_references_and_is_archived()
    {
        var h = new Harness();
        h.SeedBatchedCommand(CommandOne, CrmOne);
        h.SeedBatchedCommand(CommandTwo, CrmTwo, CommandType.OpenAccount);

        h.Transport.Inbound[AckName] = InboundBatchTestContext.File(
            InboundFileKind.Acknowledgement, 12,
            InboundBatchTestContext.AckLine(CommandOne, "OK", "CBS-C-1"),
            InboundBatchTestContext.AckLine(CommandTwo, "OK", "CBS-A-2"));

        var report = await h.RunAsync();

        report.Processed.Should().Be(1);
        report.CommandsSucceeded.Should().Be(2);
        report.Lines.Should().BeEmpty();

        await using var fresh = h.Fresh();

        (await fresh.Commands.Where(c => c.Status == CommandStatus.Succeeded).CountAsync())
            .Should().Be(2);

        var references = await fresh.References.OrderBy(r => r.ExternalId).ToListAsync();
        references.Select(r => r.ExternalId).Should().Equal("CBS-A-2", "CBS-C-1");
        references.Select(r => r.EntityType)
            .Should().Equal(IntegrationEntityTypes.Account, IntegrationEntityTypes.Customer);

        // The file is recorded under its sequence, Processed, and moved out of the directory.
        var row = await fresh.BatchFiles.SingleAsync(f => f.Direction == BatchDirection.In);
        row.SequenceNo.Should().Be(12);
        row.Status.Should().Be(BatchFileStatus.Processed);
        row.RecordCount.Should().Be(2);

        h.Transport.Archived.Should().Equal(AckName);
        h.Transport.Inbound.Should().BeEmpty();
    }

    [Fact]
    public async Task A_rejection_line_moves_the_command_to_rejected_with_its_reason()
    {
        var h = new Harness();
        h.SeedBatchedCommand(CommandOne, CrmOne);

        h.Transport.Inbound[AckName] = InboundBatchTestContext.File(
            InboundFileKind.Acknowledgement, 12,
            InboundBatchTestContext.AckLine(
                CommandOne, "KO", reasonCode: "CBS-KYC-03", reasonDetail: "Pièce illisible"));

        var report = await h.RunAsync();

        report.CommandsRejected.Should().Be(1);
        report.CommandsSucceeded.Should().Be(0);

        await using var fresh = h.Fresh();

        var command = await fresh.Commands.SingleAsync();
        command.Status.Should().Be(CommandStatus.Rejected);
        command.LastErrorMessage.Should().Contain("CBS-KYC-03");
        (await fresh.References.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task A_checksum_mismatch_marks_the_file_failed_and_applies_nothing()
    {
        var h = new Harness();
        h.SeedBatchedCommand(CommandOne, CrmOne);

        h.Transport.Inbound[AckName] = InboundBatchTestContext.FileWithBrokenChecksum(
            InboundFileKind.Acknowledgement, 12,
            InboundBatchTestContext.AckLine(CommandOne, "OK", "CBS-C-1"));

        var report = await h.RunAsync();

        report.Failed.Should().Be(1);
        report.Processed.Should().Be(0);
        report.CommandsClosed.Should().Be(0);
        report.Files.Single().Code.Should().Be(IntegrationErrors.BatchChecksumMismatch);

        await using var fresh = h.Fresh();

        // Nothing applied: a half-read acknowledgement file would close the wrong commands.
        (await fresh.Commands.SingleAsync()).Status.Should().Be(CommandStatus.Batched);
        (await fresh.References.CountAsync()).Should().Be(0);

        var row = await fresh.BatchFiles.SingleAsync(f => f.Direction == BatchDirection.In);
        row.Status.Should().Be(BatchFileStatus.Failed);
        row.FailureDetail.Should().Contain(IntegrationErrors.BatchChecksumMismatch);

        // Left in place: it is the only evidence of what the external system actually sent.
        h.Transport.Inbound.Should().ContainKey(AckName);
        h.Transport.Archived.Should().BeEmpty();
    }

    [Fact]
    public async Task A_file_whose_sequence_is_already_processed_is_skipped_with_no_effect()
    {
        var h = new Harness();
        h.SeedBatchedCommand(CommandOne, CrmOne);
        h.Seed(InboundBatchTestContext.RecordedInboundFile(
            InboundBatchTestContext.TenantA, InboundBatchTestContext.ConnectionA, 12,
            BatchFileStatus.Processed));

        h.Transport.Inbound[AckName] = InboundBatchTestContext.File(
            InboundFileKind.Acknowledgement, 12,
            InboundBatchTestContext.AckLine(CommandOne, "OK", "CBS-C-1"));

        var report = await h.RunAsync();

        report.Skipped.Should().Be(1);
        report.CommandsClosed.Should().Be(0);

        await using var fresh = h.Fresh();

        (await fresh.Commands.SingleAsync()).Status.Should().Be(CommandStatus.Batched);
        (await fresh.References.CountAsync()).Should().Be(0);

        // One row for that sequence, not a second: the unique index would have refused it.
        (await fresh.BatchFiles.CountAsync(f => f.Direction == BatchDirection.In)).Should().Be(1);
    }

    [Fact]
    public async Task A_file_whose_sequence_is_already_recorded_as_failed_is_also_skipped()
    {
        var h = new Harness();
        h.SeedBatchedCommand(CommandOne, CrmOne);
        h.Seed(InboundBatchTestContext.RecordedInboundFile(
            InboundBatchTestContext.TenantA, InboundBatchTestContext.ConnectionA, 12,
            BatchFileStatus.Failed));

        h.Transport.Inbound[AckName] = InboundBatchTestContext.File(
            InboundFileKind.Acknowledgement, 12,
            InboundBatchTestContext.AckLine(CommandOne, "OK", "CBS-C-1"));

        var report = await h.RunAsync();

        // Already refused and reported: re-reading it every quarter of an hour would be a hot
        // loop over a file only a human can fix.
        report.Skipped.Should().Be(1);
        report.Processed.Should().Be(0);
    }

    [Fact]
    public async Task A_sequence_below_one_already_processed_is_reported_not_applied()
    {
        var h = new Harness();
        h.SeedBatchedCommand(CommandOne, CrmOne);
        h.Seed(InboundBatchTestContext.RecordedInboundFile(
            InboundBatchTestContext.TenantA, InboundBatchTestContext.ConnectionA, 20,
            BatchFileStatus.Processed, fileName: "SNK-ACK-000020.csv"));

        h.Transport.Inbound[AckName] = InboundBatchTestContext.File(
            InboundFileKind.Acknowledgement, 12,
            InboundBatchTestContext.AckLine(CommandOne, "OK", "CBS-C-1"));

        var report = await h.RunAsync();

        report.Failed.Should().Be(1);
        report.Files.Single().Code.Should().Be(IntegrationErrors.BatchSequenceOutOfOrder);

        await using var fresh = h.Fresh();

        (await fresh.Commands.SingleAsync()).Status.Should().Be(CommandStatus.Batched);

        var row = await fresh.BatchFiles.SingleAsync(f => f.SequenceNo == 12);
        row.Status.Should().Be(BatchFileStatus.Failed);
        row.FailureDetail.Should().Contain(IntegrationErrors.BatchSequenceOutOfOrder);

        h.Transport.Inbound.Should().ContainKey(AckName);
    }

    [Fact]
    public async Task A_sequence_above_the_highest_processed_one_is_applied()
    {
        var h = new Harness();
        h.SeedBatchedCommand(CommandOne, CrmOne);
        h.Seed(InboundBatchTestContext.RecordedInboundFile(
            InboundBatchTestContext.TenantA, InboundBatchTestContext.ConnectionA, 5,
            BatchFileStatus.Processed, fileName: "SNK-ACK-000005.csv"));

        h.Transport.Inbound[AckName] = InboundBatchTestContext.File(
            InboundFileKind.Acknowledgement, 12,
            InboundBatchTestContext.AckLine(CommandOne, "OK", "CBS-C-1"));

        // A gap is a file that was lost, not a reason to stop: halting here would freeze every
        // later acknowledgement of the connection behind one missing file.
        (await h.RunAsync()).Processed.Should().Be(1);
    }

    [Fact]
    public async Task A_malformed_line_is_reported_and_the_rest_of_the_file_still_applies()
    {
        var h = new Harness();
        h.SeedBatchedCommand(CommandOne, CrmOne);
        h.SeedBatchedCommand(CommandTwo, CrmTwo);

        h.Transport.Inbound[AckName] = InboundBatchTestContext.File(
            InboundFileKind.Acknowledgement, 12,
            InboundBatchTestContext.AckLine(CommandOne, "OK", "CBS-C-1"),
            "this-is-not-a-line",
            "nope;MAYBE;;;",
            InboundBatchTestContext.AckLine(CommandTwo, "OK", "CBS-C-2"));

        var report = await h.RunAsync();

        report.Processed.Should().Be(1);
        report.CommandsSucceeded.Should().Be(2);
        report.Lines.Should().HaveCount(2);
        report.Lines.Should().OnlyContain(l => l.Code == InboundBatchCodes.LineMalformed);

        // The line numbers are the file's own: the header is line 1.
        report.Lines.Select(l => l.FileLine).Should().Equal(3, 4);

        await using var fresh = h.Fresh();
        (await fresh.Commands.CountAsync(c => c.Status == CommandStatus.Succeeded)).Should().Be(2);
        (await fresh.References.CountAsync()).Should().Be(2);
    }

    [Fact]
    public async Task A_file_with_no_readable_header_is_reported_and_left_in_place()
    {
        var h = new Harness();

        h.Transport.Inbound["junk.csv"] = System.Text.Encoding.UTF8.GetBytes(
            "this is not one of our files\nat all\n");

        var report = await h.RunAsync();

        report.Unreadable.Should().Be(1);
        report.Files.Single().Code.Should().Be(InboundBatchCodes.HeaderUnreadable);

        await using var fresh = h.Fresh();

        // No row: an unreadable envelope is not attributable to a sequence, and inventing one
        // would let it collide with a real file's.
        (await fresh.BatchFiles.CountAsync()).Should().Be(0);
        h.Transport.Inbound.Should().ContainKey("junk.csv");
    }

    [Fact]
    public async Task A_file_that_cannot_be_read_does_not_stop_the_others()
    {
        var h = new Harness();
        h.SeedBatchedCommand(CommandOne, CrmOne);

        h.Transport.Inbound["a-locked.csv"] = [1, 2, 3];
        h.Transport.UnreadableNames.Add("a-locked.csv");
        h.Transport.Inbound[AckName] = InboundBatchTestContext.File(
            InboundFileKind.Acknowledgement, 12,
            InboundBatchTestContext.AckLine(CommandOne, "OK", "CBS-C-1"));

        var report = await h.RunAsync();

        report.Unreadable.Should().Be(1);
        report.Processed.Should().Be(1);
        report.CommandsSucceeded.Should().Be(1);
    }

    [Fact]
    public async Task A_directory_that_cannot_be_listed_is_recorded_and_not_thrown()
    {
        var h = new Harness();
        h.Transport.ListFailureCode = IntegrationErrors.Unavailable;

        var report = await h.RunAsync();

        report.Listed.Should().Be(0);
        report.Files.Should().BeEmpty();
    }

    [Fact]
    public async Task An_extraction_calls_the_projector_once_per_named_customer()
    {
        var h = new Harness();
        h.SeedCustomerReference(CrmOne, "CBS-C-1");
        h.SeedCustomerReference(CrmTwo, "CBS-C-2");

        h.Transport.Inbound[ExtractName] = InboundBatchTestContext.File(
            InboundFileKind.Extraction, 12,
            // Further columns — a CBS's own balances — are ignored on purpose: an unauthenticated
            // file is not a write path into a customer's financial position.
            InboundBatchTestContext.ExtractionLine("CBS-C-1", "1250000", "XOF"),
            InboundBatchTestContext.ExtractionLine("CBS-C-2", "40000", "XOF"));

        var report = await h.RunAsync();

        report.Processed.Should().Be(1);
        report.CustomersProjected.Should().Be(2);

        await h.Projector.Received(1).ProjectAsync(
            InboundBatchTestContext.TenantA, InboundBatchTestContext.ConnectionA, CrmOne,
            Arg.Any<CancellationToken>());

        await h.Projector.Received(1).ProjectAsync(
            InboundBatchTestContext.TenantA, InboundBatchTestContext.ConnectionA, CrmTwo,
            Arg.Any<CancellationToken>());

        await h.Projector.Received(2).ProjectAsync(
            Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>());

        await using var fresh = h.Fresh();
        (await fresh.References.CountAsync(r => r.LastSyncedAt != null)).Should().Be(2);
    }

    [Fact]
    public async Task An_extraction_naming_an_unreferenced_customer_reports_the_line()
    {
        var h = new Harness();
        h.SeedCustomerReference(CrmOne, "CBS-C-1");

        h.Transport.Inbound[ExtractName] = InboundBatchTestContext.File(
            InboundFileKind.Extraction, 12,
            InboundBatchTestContext.ExtractionLine("CBS-C-1"),
            InboundBatchTestContext.ExtractionLine("CBS-WHO"));

        var report = await h.RunAsync();

        report.CustomersProjected.Should().Be(1);
        report.Lines.Single().Code.Should().Be(InboundBatchCodes.CustomerNotReferenced);

        await h.Projector.Received(1).ProjectAsync(
            Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task An_extraction_never_reaches_a_customer_referenced_by_another_tenant()
    {
        var h = new Harness();

        await using (var seed = InboundBatchTestContext.NewDb(
            InboundBatchTestContext.TenantB, h.DatabaseName))
        {
            seed.References.Add(IntegrationReference.Create(
                InboundBatchTestContext.TenantB, InboundBatchTestContext.ConnectionA,
                IntegrationKind.Amplitude, IntegrationEntityTypes.Customer, CrmOne, "CBS-C-1",
                h.Clock));

            await seed.SaveChangesAsync();
        }

        h.Transport.Inbound[ExtractName] = InboundBatchTestContext.File(
            InboundFileKind.Extraction, 12,
            InboundBatchTestContext.ExtractionLine("CBS-C-1"));

        var report = await h.RunAsync();

        report.CustomersProjected.Should().Be(0);
        report.Lines.Single().Code.Should().Be(InboundBatchCodes.CustomerNotReferenced);

        await h.Projector.DidNotReceive().ProjectAsync(
            Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Another_tenants_connection_is_never_polled()
    {
        var h = new Harness(seedConnection: false);

        await using (var seed = InboundBatchTestContext.NewDb(
            InboundBatchTestContext.TenantB, h.DatabaseName))
        {
            seed.Connections.Add(InboundBatchTestContext.Connection(
                InboundBatchTestContext.TenantB, InboundBatchTestContext.ConnectionB));

            await seed.SaveChangesAsync();
        }

        h.Transport.Inbound[AckName] = InboundBatchTestContext.File(
            InboundFileKind.Acknowledgement, 12, "x;OK;y;;");

        var report = await h.RunAsync();

        // Tenant A owns no connection, so nothing was listed at all.
        report.Listed.Should().Be(0);
        h.Transport.Inbound.Should().ContainKey(AckName);
    }

    [Fact]
    public async Task A_connection_that_is_not_file_based_is_not_a_target()
    {
        var name = InboundBatchTestContext.NewDbName();
        var db = InboundBatchTestContext.NewDb(InboundBatchTestContext.TenantA, name);

        var api = IntegrationConnection.Create(
            InboundBatchTestContext.TenantA, IntegrationFamily.CoreBanking, IntegrationKind.Temenos,
            IntegrationMode.Api, "Temenos", new TemenosSettings { BaseUrl = "https://t/" },
            InboundBatchTestContext.Actor,
            new InboundBatchTestContext.FixedClock(InboundBatchTestContext.Now));

        db.Connections.Add(api);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        (await InboundBatchTargets.ListAsync(db, InboundBatchTestContext.TenantA, default))
            .Should().BeEmpty();

        (await InboundBatchPollOrchestratorJob.HasFileConnectionAsync(
            db, InboundBatchTestContext.TenantA, default)).Should().BeFalse();
    }

    [Fact]
    public async Task A_file_based_connection_makes_the_tenant_a_fan_out_target()
    {
        var h = new Harness();

        (await InboundBatchPollOrchestratorJob.HasFileConnectionAsync(
            h.Db, InboundBatchTestContext.TenantA, default)).Should().BeTrue();

        (await InboundBatchPollOrchestratorJob.HasFileConnectionAsync(
            h.Db, InboundBatchTestContext.TenantB, default)).Should().BeFalse();
    }

    [Fact]
    public async Task A_connection_with_no_inbound_directory_is_not_polled_but_still_owes_alerts()
    {
        var name = InboundBatchTestContext.NewDbName();
        var db = InboundBatchTestContext.NewDb(InboundBatchTestContext.TenantA, name);

        db.Connections.Add(InboundBatchTestContext.Connection(
            InboundBatchTestContext.TenantA, InboundBatchTestContext.ConnectionA,
            inboundDirectory: null));

        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var target = (await InboundBatchTargets.ListAsync(
            db, InboundBatchTestContext.TenantA, default)).Single();

        target.CanPoll.Should().BeFalse();
    }

    [Fact]
    public async Task A_deactivated_connection_is_not_a_target()
    {
        var name = InboundBatchTestContext.NewDbName();
        var db = InboundBatchTestContext.NewDb(InboundBatchTestContext.TenantA, name);

        db.Connections.Add(InboundBatchTestContext.Connection(
            InboundBatchTestContext.TenantA, InboundBatchTestContext.ConnectionA, active: false));

        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        (await InboundBatchTargets.ListAsync(db, InboundBatchTestContext.TenantA, default))
            .Should().BeEmpty();
    }

    [Fact]
    public async Task The_poll_and_the_overdue_sweep_run_in_one_pass()
    {
        var h = new Harness();

        var fileId = Guid.NewGuid();
        h.Seed(InboundBatchTestContext.SentOutboundFile(
            InboundBatchTestContext.TenantA, InboundBatchTestContext.ConnectionA, fileId,
            sequenceNo: 3, sentHoursAgo: 72));

        h.Seed(InboundBatchTestContext.BatchedCommand(
            InboundBatchTestContext.TenantA, InboundBatchTestContext.ConnectionA, fileId));

        h.SeedBatchedCommand(CommandOne, CrmOne);
        h.Transport.Inbound[AckName] = InboundBatchTestContext.File(
            InboundFileKind.Acknowledgement, 12,
            InboundBatchTestContext.AckLine(CommandOne, "OK", "CBS-C-1"));

        var report = await h.RunAsync();

        report.Processed.Should().Be(1);
        report.CommandsSucceeded.Should().Be(1);
        report.AckOverdueAlerts.Should().Be(1);
        h.Alerter.Alerts.Single().BatchFileId.Should().Be(fileId);
    }

    /// <summary>
    /// One connection of tenant A, an in-memory directory, a substituted projector and a
    /// recording alerter — the collaborators <c>RunAsync</c> takes, with nothing resolved.
    /// </summary>
    private sealed class Harness
    {
        public Harness(bool seedConnection = true)
        {
            DatabaseName = InboundBatchTestContext.NewDbName();
            Db = InboundBatchTestContext.NewDb(InboundBatchTestContext.TenantA, DatabaseName);

            if (!seedConnection) return;

            Db.Connections.Add(InboundBatchTestContext.Connection(
                InboundBatchTestContext.TenantA, InboundBatchTestContext.ConnectionA));

            Db.SaveChanges();
            Db.ChangeTracker.Clear();
        }

        public string DatabaseName { get; }

        public IntegrationDbContext Db { get; }

        public TimeProvider Clock { get; } =
            new InboundBatchTestContext.FixedClock(InboundBatchTestContext.Now);

        public InboundBatchTestContext.FakeFileTransport Transport { get; } = new();

        public ICbsSnapshotProjector Projector { get; } = Substitute.For<ICbsSnapshotProjector>();

        public InboundBatchTestContext.RecordingAlerter Alerter { get; } = new();

        public void Seed<T>(T entity) where T : class
        {
            Db.Add(entity);
            Db.SaveChanges();
            Db.ChangeTracker.Clear();
        }

        public void SeedBatchedCommand(
            Guid commandId, Guid crmId, CommandType type = CommandType.CreateCustomer)
            => Seed(InboundBatchTestContext.BatchedCommand(
                InboundBatchTestContext.TenantA, InboundBatchTestContext.ConnectionA,
                batchFileId: Guid.NewGuid(), id: commandId, crmId: crmId, type: type));

        public void SeedCustomerReference(Guid crmId, string externalId)
            => Seed(IntegrationReference.Create(
                InboundBatchTestContext.TenantA, InboundBatchTestContext.ConnectionA,
                IntegrationKind.Amplitude, IntegrationEntityTypes.Customer, crmId, externalId,
                Clock));

        public Task<InboundBatchPollReport> RunAsync()
            => PollInboundBatchFilesJob.RunAsync(
                Db, Transport, Projector, Alerter, Clock, NullLogger.Instance,
                InboundBatchTestContext.TenantA, CancellationToken.None);

        public IntegrationDbContext Fresh()
            => InboundBatchTestContext.NewDb(InboundBatchTestContext.TenantA, DatabaseName);
    }
}
