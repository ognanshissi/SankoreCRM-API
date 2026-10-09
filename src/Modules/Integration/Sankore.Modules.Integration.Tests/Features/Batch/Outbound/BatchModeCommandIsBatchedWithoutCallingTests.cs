namespace Sankore.Modules.Integration.Tests.Features.Batch.Outbound;

using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Integration.Adapters.Fake;
using Sankore.Modules.Integration.Domain;
using Sankore.Modules.Integration.Features.Batch.Outbound;
using Sankore.Modules.Integration.Features.Commands.Execute;
using Sankore.Modules.Integration.PublicApi;
using Sankore.Modules.Integration.Tests.Features.Commands;
using Sankore.Modules.Integration.Tests.TestSupport;
using Xunit;

/// <summary>
/// <b>Criterion 1</b>: in batch mode, executing a command moves it to <c>Batched</c> WITHOUT
/// calling the external system.
///
/// <para>
/// The absence of the call is the property, so every assertion here is on the recording adapter's
/// empty <c>Calls</c> list rather than on anything the handler returns. An assertion on the
/// status alone would pass just as happily if the adapter had been called first and the command
/// batched afterwards — which is precisely the bug worth preventing, since a batch CBS that also
/// answered an API call would then receive the same write twice.
/// </para>
///
/// <para>
/// The control case is in the same file on purpose: the SAME command type against an
/// <c>Api</c>-mode connection DOES record a call. Without it, an adapter that was never reachable
/// for an unrelated reason would make the batch assertion vacuous.
/// </para>
/// </summary>
public sealed class BatchModeCommandIsBatchedWithoutCallingTests
{
    private static readonly Guid Tenant = new("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    private static readonly Guid ConnectionId = new("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
    private static readonly Guid Customer = new("dddddddd-dddd-dddd-dddd-dddddddddddd");

    [Fact]
    public async Task A_batch_mode_command_is_batched_and_no_port_is_called()
    {
        var adapter = new FakeAdapter();

        using var factory = new TestIntegrationDbContextFactory(Tenant);

        var commandId = Guid.NewGuid();

        await using (var seed = factory.CreateContext())
        {
            seed.Connections.Add(OutboundBatchTestContext.BatchConnection(
                Tenant, OutboundBatchTestContext.BeforeCutOff, id: ConnectionId));

            seed.Commands.Add(OutboundBatchTestContext.Queued(
                Tenant, ConnectionId, OutboundBatchTestContext.BeforeCutOff,
                crmId: Customer, id: commandId));

            await seed.SaveChangesAsync();
        }

        await using var db = factory.CreateContext();

        // The real enlister over the real generator: the point is that THIS path, the one the
        // dispatcher takes, reaches no port.
        var handler = BatchHandler(db, adapter, OutboundBatchTestContext.AfterCutOff);

        var result = await handler.Handle(
            new ExecuteIntegrationCommandCommand(commandId, Tenant), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Status.Should().Be(nameof(CommandStatus.Batched));

        // THE criterion.
        adapter.Calls.Should().BeEmpty(
            "a batch connection has no endpoint to call; the write leaves in a file");

        var command = await db.Commands.IgnoreQueryFilters().SingleAsync();
        command.Status.Should().Be(CommandStatus.Batched);
        command.BatchFileId.Should().NotBeNull();
        command.ExternalResponseRef.Should().BeNull("nothing answered, because nothing was asked");

        var file = await db.BatchFiles.IgnoreQueryFilters().SingleAsync();
        command.BatchFileId.Should().Be(file.Id);
        file.RecordCount.Should().Be(1);
        file.Status.Should().Be(
            BatchFileStatus.Generated, "the deposit belongs to the batch job, not to the dispatcher");
    }

    [Fact]
    public async Task The_control_case_an_api_mode_command_does_reach_the_port()
    {
        var adapter = new FakeAdapter();

        using var factory = new TestIntegrationDbContextFactory(Tenant);

        var commandId = Guid.NewGuid();

        await using (var seed = factory.CreateContext())
        {
            // A Fake-kind, Api-mode connection: the adapter above is registered for it.
            seed.Connections.Add(CommandsTestHarness.Connection(
                Tenant, mode: IntegrationMode.Api, id: ConnectionId));

            seed.Commands.Add(OutboundBatchTestContext.Queued(
                Tenant, ConnectionId, OutboundBatchTestContext.BeforeCutOff,
                crmId: Customer, id: commandId));

            await seed.SaveChangesAsync();
        }

        await using var db = factory.CreateContext();

        var handler = CommandsTestHarness.ExecuteHandler(
            db, adapter, Tenant, Customer);

        await handler.Handle(
            new ExecuteIntegrationCommandCommand(commandId, Tenant), CancellationToken.None);

        adapter.Calls.Should().NotBeEmpty(
            "otherwise the batch assertion above would hold for an adapter nothing can reach");
    }

    [Fact]
    public async Task A_command_created_after_the_cut_off_stays_queued_and_still_calls_nothing()
    {
        var adapter = new FakeAdapter();

        using var factory = new TestIntegrationDbContextFactory(Tenant);

        var commandId = Guid.NewGuid();

        await using (var seed = factory.CreateContext())
        {
            seed.Connections.Add(OutboundBatchTestContext.BatchConnection(
                Tenant, OutboundBatchTestContext.BeforeCutOff, id: ConnectionId));

            // Created after today's cut-off: it belongs to tomorrow's file.
            seed.Commands.Add(OutboundBatchTestContext.Queued(
                Tenant, ConnectionId, OutboundBatchTestContext.AfterCutOff,
                crmId: Customer, id: commandId));

            await seed.SaveChangesAsync();
        }

        await using var db = factory.CreateContext();

        var handler = BatchHandler(db, adapter, OutboundBatchTestContext.AfterCutOff.AddMinutes(10));

        await handler.Handle(
            new ExecuteIntegrationCommandCommand(commandId, Tenant), CancellationToken.None);

        adapter.Calls.Should().BeEmpty();

        var command = await db.Commands.IgnoreQueryFilters().SingleAsync();

        command.Status.Should().Be(
            CommandStatus.RetryScheduled,
            "a valid command whose cut-off has not come waits; it is not rejected");
        command.BatchFileId.Should().BeNull();
        command.LastErrorFamily.Should().Be(ErrorFamily.Transient);

        (await db.BatchFiles.IgnoreQueryFilters().CountAsync()).Should().Be(0);
    }

    /// <summary>
    /// The dispatcher's handler with INT-24's real enlister in place of
    /// <c>UnavailableBatchFileEnlister</c>. Everything else is <c>CommandsTestHarness</c>'s own
    /// wiring, so this suite differs from the INT-05 suites in exactly one collaborator.
    /// </summary>
    private static ExecuteIntegrationCommandHandler BatchHandler(
        Sankore.Modules.Integration.Infrastructure.IntegrationDbContext db,
        FakeAdapter adapter,
        DateTimeOffset now)
        => CommandsTestHarness.ExecuteHandler(
            db, adapter, Tenant, Customer,
            batch: new OutboundBatchFileEnlister(
                OutboundBatchTestContext.Generator(db, now),
                NullLogger<OutboundBatchFileEnlister>.Instance));
}
