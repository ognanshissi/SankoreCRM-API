namespace Sankore.Modules.Integration.Tests.Features.Batch.Outbound;

using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Integration.Domain;
using Sankore.Modules.Integration.Tests.TestSupport;
using Xunit;

/// <summary>
/// Criterion 5, second half: the content is purged after the acknowledgement AND a retention
/// delay, and the ROW survives — because the checksum and the record count are what let an
/// inspection a year later confirm what was sent without the platform still holding the personal
/// data.
/// </summary>
public sealed class OutboundBatchPurgeTests
{
    private static readonly Guid Tenant = new("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    private static readonly Guid ConnectionId = new("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");

    [Fact]
    public async Task An_acknowledged_file_past_its_retention_loses_its_content_and_keeps_its_row()
    {
        var (store, backend) = OutboundBatchTestContext.Store();

        using var factory = new TestIntegrationDbContextFactory(Tenant);

        await using (var seed = factory.CreateContext())
        {
            seed.Connections.Add(OutboundBatchTestContext.BatchConnection(
                Tenant, OutboundBatchTestContext.BeforeCutOff, id: ConnectionId, retentionDays: 30));

            seed.Commands.Add(OutboundBatchTestContext.Queued(
                Tenant, ConnectionId, OutboundBatchTestContext.BeforeCutOff));

            await seed.SaveChangesAsync();
        }

        long sequence;
        string checksum;
        int recordCount;
        string fileName;

        // Generate, deposit and acknowledge, all on the cut-off day.
        await using (var db = factory.CreateContext())
        {
            var connection = await db.Connections.IgnoreQueryFilters().SingleAsync();

            var generator = OutboundBatchTestContext.Generator(
                db, OutboundBatchTestContext.AfterCutOff, store);

            await generator.GenerateAsync(Tenant, connection, null, CancellationToken.None);
            await generator.DepositAsync(Tenant, connection, CancellationToken.None);

            var file = await db.BatchFiles.AsTracking().IgnoreQueryFilters().SingleAsync();

            file.MarkAcknowledged(
                new OutboundBatchTestContext.FixedClock(OutboundBatchTestContext.AfterCutOff));

            await db.SaveChangesAsync();

            sequence = file.SequenceNo;
            checksum = file.ChecksumSha256;
            recordCount = file.RecordCount;
            fileName = file.FileName;
        }

        backend.Keys.Should().HaveCount(1, "the content is still stored before the purge");

        // One day later: acknowledged, but inside the 30-day retention.
        await using (var db = factory.CreateContext())
        {
            var connection = await db.Connections.IgnoreQueryFilters().SingleAsync();

            var purged = await OutboundBatchTestContext
                .Generator(db, OutboundBatchTestContext.AfterCutOff.AddDays(1), store)
                .PurgeAsync(Tenant, connection, CancellationToken.None);

            purged.Should().Be(0, "the retention delay has not elapsed");
        }

        backend.Keys.Should().HaveCount(1);

        // Thirty-one days later.
        await using (var db = factory.CreateContext())
        {
            var connection = await db.Connections.IgnoreQueryFilters().SingleAsync();

            var purged = await OutboundBatchTestContext
                .Generator(db, OutboundBatchTestContext.AfterCutOff.AddDays(31), store)
                .PurgeAsync(Tenant, connection, CancellationToken.None);

            purged.Should().Be(1);
        }

        backend.Keys.Should().BeEmpty("the content is deleted");

        await using (var verify = factory.CreateContext())
        {
            var file = await verify.BatchFiles.IgnoreQueryFilters().SingleAsync();

            file.Status.Should().Be(BatchFileStatus.Purged);
            file.StorageRef.Should().BeNull();

            // The evidence survives, which is the whole point of purging rather than deleting.
            file.SequenceNo.Should().Be(sequence);
            file.ChecksumSha256.Should().Be(checksum);
            file.RecordCount.Should().Be(recordCount);
            file.FileName.Should().Be(fileName);
            file.AckReceivedAt.Should().NotBeNull();
        }
    }

    [Fact]
    public async Task A_file_that_was_never_acknowledged_is_not_purged_however_old()
    {
        var (store, backend) = OutboundBatchTestContext.Store();

        using var factory = new TestIntegrationDbContextFactory(Tenant);

        await using (var seed = factory.CreateContext())
        {
            seed.Connections.Add(OutboundBatchTestContext.BatchConnection(
                Tenant, OutboundBatchTestContext.BeforeCutOff, id: ConnectionId, retentionDays: 1));

            seed.Commands.Add(OutboundBatchTestContext.Queued(
                Tenant, ConnectionId, OutboundBatchTestContext.BeforeCutOff));

            await seed.SaveChangesAsync();
        }

        await using (var db = factory.CreateContext())
        {
            var connection = await db.Connections.IgnoreQueryFilters().SingleAsync();

            var generator = OutboundBatchTestContext.Generator(
                db, OutboundBatchTestContext.AfterCutOff, store);

            await generator.GenerateAsync(Tenant, connection, null, CancellationToken.None);
            await generator.DepositAsync(Tenant, connection, CancellationToken.None);
        }

        await using (var db = factory.CreateContext())
        {
            var connection = await db.Connections.IgnoreQueryFilters().SingleAsync();

            var purged = await OutboundBatchTestContext
                .Generator(db, OutboundBatchTestContext.AfterCutOff.AddYears(1), store)
                .PurgeAsync(Tenant, connection, CancellationToken.None);

            purged.Should().Be(
                0,
                "a file the CBS never confirmed may still have to be re-deposited; purging it "
                + "would leave a write owed with no way to make it");
        }

        backend.Keys.Should().HaveCount(1);

        (await factory.CreateContext().BatchFiles.IgnoreQueryFilters().SingleAsync())
            .Status.Should().Be(BatchFileStatus.Sent);
    }

    [Fact]
    public async Task A_retention_of_zero_takes_the_default_rather_than_purging_immediately()
    {
        var (store, backend) = OutboundBatchTestContext.Store();

        using var factory = new TestIntegrationDbContextFactory(Tenant);

        await using (var seed = factory.CreateContext())
        {
            seed.Connections.Add(OutboundBatchTestContext.BatchConnection(
                Tenant, OutboundBatchTestContext.BeforeCutOff, id: ConnectionId, retentionDays: 0));

            seed.Commands.Add(OutboundBatchTestContext.Queued(
                Tenant, ConnectionId, OutboundBatchTestContext.BeforeCutOff));

            await seed.SaveChangesAsync();
        }

        await using (var db = factory.CreateContext())
        {
            var connection = await db.Connections.IgnoreQueryFilters().SingleAsync();

            var generator = OutboundBatchTestContext.Generator(
                db, OutboundBatchTestContext.AfterCutOff, store);

            await generator.GenerateAsync(Tenant, connection, null, CancellationToken.None);
            await generator.DepositAsync(Tenant, connection, CancellationToken.None);

            var file = await db.BatchFiles.AsTracking().IgnoreQueryFilters().SingleAsync();
            file.MarkAcknowledged(
                new OutboundBatchTestContext.FixedClock(OutboundBatchTestContext.AfterCutOff));
            await db.SaveChangesAsync();
        }

        await using (var db = factory.CreateContext())
        {
            var connection = await db.Connections.IgnoreQueryFilters().SingleAsync();

            // One second after the acknowledgement. A literal zero would purge it here.
            var purged = await OutboundBatchTestContext
                .Generator(db, OutboundBatchTestContext.AfterCutOff.AddSeconds(1), store)
                .PurgeAsync(Tenant, connection, CancellationToken.None);

            purged.Should().Be(
                0, "a misconfigured zero must not mean 'delete the evidence immediately'");
        }

        backend.Keys.Should().HaveCount(1);
    }
}
