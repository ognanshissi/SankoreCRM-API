namespace Sankore.Modules.Integration.Tests.Features.Commands;

using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Sankore.Modules.Integration.Adapters.Fake;
using Sankore.Modules.Integration.Domain;
using Sankore.Modules.Integration.Features.Batch.Outbound;
using Sankore.Modules.Integration.Features.Commands.Execute;
using Sankore.Modules.Integration.PublicApi;
using Sankore.Modules.Integration.Tests.Features.Batch.Outbound;
using Sankore.Modules.Integration.Tests.TestSupport;
using Xunit;

/// <summary>
/// What the dispatcher does with a connection in <see cref="IntegrationMode.Relay"/>.
///
/// <para>
/// <b>The defect these tests close.</b> The routing branch read <c>Mode == Batch</c> and nothing
/// else, so a Relay connection fell through to <c>ResolveAdapter</c> and the dispatcher called the
/// core banking system <b>directly, from this process</b> — the single thing that mode exists to
/// prevent, since its premise is that the CBS sits inside the institution's network and is not
/// reachable from here. On a Temenos row it was worse than a failed call: that transport performs
/// no egress validation in <c>Api</c> mode, so a <c>baseUrl</c> on a private address would have
/// the platform open a connection inside its OWN network, with the mode making it read as
/// sanctioned. Nothing in the suite noticed, because no test had ever dispatched a command on a
/// Relay connection — the mode was exercised only by the file transport's own routing tests.
/// </para>
///
/// <para>
/// Relay has two halves and they are at different stages, which is why the fix is not simply
/// "refuse Relay": the file carrier is delivered (<c>RelayFileTransport</c>, and
/// <c>IntegrationFileTransportRouter</c> already sends a Relay connection's deposits to the
/// agent), while the order channel is not (INT-26's platform side). So a Relay connection holding
/// batch coordinates must behave exactly like a Batch one, and any other must be refused. Both
/// halves are asserted here; asserting only the refusal would have passed on a version that broke
/// batch-over-relay, which is a delivered feature.
/// </para>
/// </summary>
public sealed class RelayCommandRoutingTests
{
    private static readonly Guid Tenant = new("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    private static readonly Guid ConnectionId = new("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
    private static readonly Guid Customer = new("dddddddd-dddd-dddd-dddd-dddddddddddd");
    private static readonly Guid Agent = new("55555555-5555-5555-5555-555555555555");

    [Fact]
    public async Task A_relay_connection_with_no_batch_coordinates_sends_nothing_and_is_rejected()
    {
        var adapter = new FakeAdapter();

        using var factory = new TestIntegrationDbContextFactory(Tenant);
        var commandId = Guid.NewGuid();

        await using (var seed = factory.CreateContext())
        {
            // Fake-kind settings are not BatchCapableSettings, which is the shape that matters:
            // a connection with no file coordinates has nowhere for this write to go but a call.
            seed.Connections.Add(CommandsTestHarness.Connection(
                Tenant, mode: IntegrationMode.Relay, id: ConnectionId));

            seed.Commands.Add(OutboundBatchTestContext.Queued(
                Tenant, ConnectionId, CommandsTestHarness.Now, crmId: Customer, id: commandId));

            await seed.SaveChangesAsync();
        }

        await using var db = factory.CreateContext();

        var handler = CommandsTestHarness.ExecuteHandler(db, adapter, Tenant, Customer);

        await handler.Handle(
            new ExecuteIntegrationCommandCommand(commandId, Tenant), CancellationToken.None);

        // THE criterion, and it is the empty call list rather than the status: a version that
        // called the CBS and then recorded a rejection would satisfy any assertion on the status
        // alone, while having already done the thing that must not happen.
        adapter.Calls.Should().BeEmpty(
            "a relay connection's write must never leave this process directly");

        var command = await db.Commands.IgnoreQueryFilters().SingleAsync();

        // Rejected and not retried: the order channel is not disconnected, it does not exist, so
        // no number of attempts produces a different answer. The rejection is what reaches an
        // administrator through IntegrationCommandRejectedEvent — the only way anybody learns the
        // connection's mode is wrong.
        command.Status.Should().Be(CommandStatus.Rejected);
        command.LastErrorFamily.Should().Be(ErrorFamily.Technical);
        command.LastErrorMessage.Should().Contain(IntegrationErrors.RelayCommandChannelMissing);
    }

    [Fact]
    public async Task The_refusal_tells_an_administrator_which_three_things_would_fix_it()
    {
        var adapter = new FakeAdapter();

        using var factory = new TestIntegrationDbContextFactory(Tenant);
        var commandId = Guid.NewGuid();

        await using (var seed = factory.CreateContext())
        {
            seed.Connections.Add(CommandsTestHarness.Connection(
                Tenant, mode: IntegrationMode.Relay, id: ConnectionId));
            seed.Commands.Add(OutboundBatchTestContext.Queued(
                Tenant, ConnectionId, CommandsTestHarness.Now, crmId: Customer, id: commandId));
            await seed.SaveChangesAsync();
        }

        await using var db = factory.CreateContext();
        await CommandsTestHarness.ExecuteHandler(db, adapter, Tenant, Customer).Handle(
            new ExecuteIntegrationCommandCommand(commandId, Tenant), CancellationToken.None);

        var message = (await db.Commands.IgnoreQueryFilters().SingleAsync()).LastErrorMessage;

        // A rejection nobody can act on is a rejection that sits in the queue. Two of the three
        // remedies are the administrator's own and one is not, and the message has to say which —
        // otherwise every occurrence becomes a support ticket asking whether to wait.
        message.Should().Contain("INT-26", "the undelivered lot must be named, not implied");
        message.Should().Contain("batch coordinates");
        message.Should().Contain("nothing is sent from here");
    }

    [Fact]
    public async Task A_relay_connection_with_batch_coordinates_is_batched_exactly_like_a_batch_one()
    {
        var adapter = new FakeAdapter();

        using var factory = new TestIntegrationDbContextFactory(Tenant);
        var commandId = Guid.NewGuid();

        await using (var seed = factory.CreateContext())
        {
            // Mode Relay AND a relay agent AND file coordinates: batch-over-relay, which is
            // INT-24 plus INT-26's delivered half. The file is produced here and deposited by the
            // agent, never by us.
            seed.Connections.Add(OutboundBatchTestContext.BatchConnection(
                Tenant, OutboundBatchTestContext.BeforeCutOff, id: ConnectionId, relayAgentId: Agent));

            seed.Commands.Add(OutboundBatchTestContext.Queued(
                Tenant, ConnectionId, OutboundBatchTestContext.BeforeCutOff,
                crmId: Customer, id: commandId));

            await seed.SaveChangesAsync();
        }

        await using var db = factory.CreateContext();

        var handler = CommandsTestHarness.ExecuteHandler(
            db, adapter, Tenant, Customer,
            batch: new OutboundBatchFileEnlister(
                OutboundBatchTestContext.Generator(db, OutboundBatchTestContext.AfterCutOff),
                NullLogger<OutboundBatchFileEnlister>.Instance),
            clock: new OutboundBatchTestContext.FixedClock(OutboundBatchTestContext.AfterCutOff));

        await handler.Handle(
            new ExecuteIntegrationCommandCommand(commandId, Tenant), CancellationToken.None);

        adapter.Calls.Should().BeEmpty("the write leaves in a file, so no port is touched");

        var command = await db.Commands.IgnoreQueryFilters().SingleAsync();

        // Batched, NOT rejected. This is the half a blanket "refuse every Relay connection" would
        // have broken, and it is a delivered feature: before the fix these commands were sent to
        // an adapter instead, which for Perfect Vision and Amplitude meant a rejection naming a
        // missing vendor document — a wrong diagnosis for a connection that was configured
        // correctly.
        command.Status.Should().Be(CommandStatus.Batched);
        command.BatchFileId.Should().NotBeNull();
        command.LastErrorMessage.Should().BeNull();

        var file = await db.BatchFiles.IgnoreQueryFilters().SingleAsync();
        command.BatchFileId.Should().Be(file.Id);
        file.RecordCount.Should().Be(1);
    }

    [Fact]
    public async Task The_control_case_an_api_connection_still_reaches_its_adapter()
    {
        var adapter = new FakeAdapter();

        using var factory = new TestIntegrationDbContextFactory(Tenant);
        var commandId = Guid.NewGuid();

        await using (var seed = factory.CreateContext())
        {
            seed.Connections.Add(CommandsTestHarness.Connection(
                Tenant, mode: IntegrationMode.Api, id: ConnectionId));
            seed.Commands.Add(OutboundBatchTestContext.Queued(
                Tenant, ConnectionId, CommandsTestHarness.Now, crmId: Customer, id: commandId));
            await seed.SaveChangesAsync();
        }

        await using var db = factory.CreateContext();
        await CommandsTestHarness.ExecuteHandler(db, adapter, Tenant, Customer).Handle(
            new ExecuteIntegrationCommandCommand(commandId, Tenant), CancellationToken.None);

        // Without this, every assertion above would hold just as well on a dispatcher that had
        // stopped calling anything at all.
        adapter.Calls.Should().NotBeEmpty();
    }
}
