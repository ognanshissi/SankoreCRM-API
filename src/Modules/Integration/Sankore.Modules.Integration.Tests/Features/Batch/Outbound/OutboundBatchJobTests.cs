namespace Sankore.Modules.Integration.Tests.Features.Batch.Outbound;

using System.Reflection;
using FluentAssertions;
using Hangfire;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Sankore.Modules.Integration.Domain;
using Sankore.Modules.Integration.Features.Batch.Outbound;
using Sankore.Modules.Integration.PublicApi;
using Sankore.Modules.Integration.Tests.TestSupport;
using Xunit;

/// <summary>
/// Criteria 2 and 6: a job per tenant produces the file at the cut-off, and both jobs sit on the
/// <c>integration-batch</c> queue.
/// </summary>
public sealed class OutboundBatchJobTests
{
    private static readonly Guid Tenant = new("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    private static readonly Guid ConnectionId = new("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");

    [Fact]
    public void The_per_tenant_job_is_on_the_integration_batch_queue()
    {
        // Criterion 6, read off the attribute rather than asserted on a constant: the constant
        // being right is worthless if the attribute does not carry it, and a job with no [Queue]
        // lands in "default" behind the interactive work.
        var queue = typeof(GenerateTenantOutboundBatchFilesJob)
            .GetCustomAttribute<QueueAttribute>();

        queue.Should().NotBeNull();
        queue!.Queue.Should().Be("integration-batch");

        GenerateTenantOutboundBatchFilesJob.QueueName.Should().Be("integration-batch");
    }

    [Fact]
    public void The_queue_is_one_the_host_is_told_to_configure()
    {
        // The queue name has to appear in the list DispatchServiceRegistration hands the host.
        // Without that wiring the job is enqueued to a queue no worker reads and never runs at
        // all, while every test in this folder still passes — so this is the one assertion that
        // can catch the drift.
        var queues = typeof(Sankore.Modules.Integration.Features.Dispatch.DispatchTenantCommandsJob)
            .Assembly
            .GetType("Sankore.Modules.Integration.Features.Dispatch.DispatchServiceRegistration")!
            .GetField("Queues", BindingFlags.Public | BindingFlags.Static)!
            .GetValue(null) as string[];

        queues.Should().Contain(GenerateTenantOutboundBatchFilesJob.QueueName);
    }

    [Fact]
    public void The_orchestrators_recurring_id_is_hangfire_safe()
    {
        // Hangfire derives distributed lock names from the recurring-job id: dots, colons or a URL
        // in it break them. A documented pitfall of this repository.
        var id = OutboundBatchOrchestratorJob.RecurringJobId;

        id.Should().Be("integration-outbound-batch-orchestrator");
        id.Should().MatchRegex("^[a-z0-9-]+$");

        OutboundBatchOrchestratorJob.CronExpression.Should().Be("*/5 * * * *");
    }

    [Fact]
    public async Task The_pass_generates_deposits_and_purges_in_one_run()
    {
        var (store, backend) = OutboundBatchTestContext.Store();
        var transport = new RecordingFileTransport();

        using var factory = new TestIntegrationDbContextFactory(Tenant);

        await using (var seed = factory.CreateContext())
        {
            seed.Connections.Add(OutboundBatchTestContext.BatchConnection(
                Tenant, OutboundBatchTestContext.BeforeCutOff, id: ConnectionId, retentionDays: 30));

            seed.Commands.Add(OutboundBatchTestContext.Queued(
                Tenant, ConnectionId, OutboundBatchTestContext.BeforeCutOff));

            await seed.SaveChangesAsync();
        }

        // One pass at the cut-off: generate, then deposit.
        await using (var db = factory.CreateContext())
        {
            var report = await GenerateTenantOutboundBatchFilesJob.RunAsync(
                db,
                OutboundBatchTestContext.Generator(
                    db, OutboundBatchTestContext.AfterCutOff, store, transport),
                NullLogger.Instance,
                Tenant,
                CancellationToken.None);

            report.Connections.Should().Be(1);
            report.Generated.Should().Be(1);
            report.Deposited.Should().Be(1);
            report.DepositFailures.Should().Be(0);
            report.Purged.Should().Be(0);
            report.Errors.Should().Be(0);
        }

        transport.Deposits.Should().HaveCount(1);

        // The CBS acknowledges.
        await using (var db = factory.CreateContext())
        {
            var file = await db.BatchFiles.AsTracking().IgnoreQueryFilters().SingleAsync();
            file.MarkAcknowledged(
                new OutboundBatchTestContext.FixedClock(OutboundBatchTestContext.AfterCutOff));
            await db.SaveChangesAsync();
        }

        // A pass well past the retention: nothing new to generate, nothing to deposit, one purge.
        await using (var db = factory.CreateContext())
        {
            var report = await GenerateTenantOutboundBatchFilesJob.RunAsync(
                db,
                OutboundBatchTestContext.Generator(
                    db, OutboundBatchTestContext.AfterCutOff.AddDays(40), store, transport),
                NullLogger.Instance,
                Tenant,
                CancellationToken.None);

            report.Generated.Should().Be(0, "there is no command left to send");
            report.Deposited.Should().Be(0);
            report.Purged.Should().Be(1);
        }

        backend.Keys.Should().BeEmpty();
        transport.Deposits.Should().HaveCount(1, "a purge deposits nothing");
    }

    [Fact]
    public async Task An_inactive_or_non_batch_connection_is_skipped()
    {
        using var factory = new TestIntegrationDbContextFactory(Tenant);

        var inactiveId = new Guid("cccccccc-cccc-cccc-cccc-cccccccccccc");

        await using (var seed = factory.CreateContext())
        {
            // Deactivated: it owes no deposit, and generating for one would write a file nobody
            // will fetch.
            seed.Connections.Add(OutboundBatchTestContext.BatchConnection(
                Tenant, OutboundBatchTestContext.BeforeCutOff, id: inactiveId, active: false));

            // Api mode: its commands go to an adapter, not to a file.
            seed.Connections.Add(Sankore.Modules.Integration.Tests.Features.Commands
                .CommandsTestHarness.Connection(
                    Tenant, mode: IntegrationMode.Api, id: ConnectionId));

            seed.Commands.Add(OutboundBatchTestContext.Queued(
                Tenant, inactiveId, OutboundBatchTestContext.BeforeCutOff));
            seed.Commands.Add(OutboundBatchTestContext.Queued(
                Tenant, ConnectionId, OutboundBatchTestContext.BeforeCutOff));

            await seed.SaveChangesAsync();
        }

        await using var db = factory.CreateContext();

        var report = await GenerateTenantOutboundBatchFilesJob.RunAsync(
            db,
            OutboundBatchTestContext.Generator(db, OutboundBatchTestContext.AfterCutOff),
            NullLogger.Instance,
            Tenant,
            CancellationToken.None);

        report.Connections.Should().Be(0);
        report.Generated.Should().Be(0);

        (await db.BatchFiles.IgnoreQueryFilters().CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task The_orchestrator_only_fans_out_tenants_that_exchange_files()
    {
        var withBatch = new Guid("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
        var withoutBatch = new Guid("eeeeeeee-eeee-eeee-eeee-eeeeeeeeeeee");

        using var factory = new TestIntegrationDbContextFactory(withBatch);

        await using (var seed = factory.CreateContext())
        {
            seed.Connections.Add(OutboundBatchTestContext.BatchConnection(
                withBatch, OutboundBatchTestContext.BeforeCutOff, id: ConnectionId));

            seed.Connections.Add(Sankore.Modules.Integration.Tests.Features.Commands
                .CommandsTestHarness.Connection(withoutBatch, mode: IntegrationMode.Api));

            await seed.SaveChangesAsync();
        }

        await using var db = factory.CreateContext();

        (await OutboundBatchOrchestratorJob.HasBatchConnectionAsync(
            db, withBatch, CancellationToken.None)).Should().BeTrue();

        (await OutboundBatchOrchestratorJob.HasBatchConnectionAsync(
            db, withoutBatch, CancellationToken.None)).Should().BeFalse(
            "fanning out unconditionally would put tens of thousands of no-op jobs a day through "
            + "the shared Hangfire tables");
    }
}
