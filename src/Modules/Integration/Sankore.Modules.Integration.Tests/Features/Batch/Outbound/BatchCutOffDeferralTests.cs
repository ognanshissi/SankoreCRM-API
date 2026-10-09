namespace Sankore.Modules.Integration.Tests.Features.Batch.Outbound;

using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Sankore.Modules.Integration.Adapters.Fake;
using Sankore.Modules.Integration.Domain;
using Sankore.Modules.Integration.Features.Batch.Outbound;
using Sankore.Modules.Integration.Features.Commands.Execute;
using Sankore.Modules.Integration.Infrastructure;
using Sankore.Modules.Integration.PublicApi;
using Sankore.Modules.Integration.Tests.Features.Commands;
using Sankore.Modules.Integration.Tests.TestSupport;
using Xunit;

/// <summary>
/// Waiting for a cut-off must not spend the retry budget — otherwise a daily batch connection
/// loses its writes outright rather than merely delaying them.
///
/// <para>
/// The arithmetic that makes this a defect and not a preference: a command created at 09:00 is
/// not eligible for the cycle the dispatcher is in (that cycle's cut-off is YESTERDAY 18:00), so
/// every morning dispatch of every command gets the "not due" answer. Counted as attempts, the
/// eight of them are spent by midday at a one-hour cap, the command reaches <c>Rejected</c> —
/// and <c>Rejected</c> is not in the generator's eligible set, so the 18:00 file is written
/// without it and the write never leaves at all. On a daily cycle that is most commands.
/// </para>
///
/// <para>
/// So the assertions here are about the ATTEMPT COUNT and about the command still being picked up
/// by the scheduled generation, not about the status alone: <c>RetryScheduled</c> is what the
/// broken behaviour produced too, on its way to rejection.
/// </para>
/// </summary>
public sealed class BatchCutOffDeferralTests
{
    private static readonly Guid Tenant = new("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    private static readonly Guid ConnectionId = new("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
    private static readonly Guid Customer = new("dddddddd-dddd-dddd-dddd-dddddddddddd");

    /// <summary>09:00 on 11 March 2026 — the morning dispatch, nine hours before the cut-off.</summary>
    private static readonly DateTimeOffset Morning = OutboundBatchTestContext.BeforeCutOff;

    /// <summary>The cut-off this command is actually waiting for: 18:00 the same day.</summary>
    private static readonly DateTimeOffset Cycle = new(2026, 3, 11, 18, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Waiting_for_the_cut_off_spends_no_attempt_and_parks_the_command_on_it()
    {
        using var factory = new TestIntegrationDbContextFactory(Tenant);
        var commandId = Guid.NewGuid();
        await SeedAsync(factory, commandId);

        await using var db = factory.CreateContext();
        await Dispatch(db, Morning).Handle(
            new ExecuteIntegrationCommandCommand(commandId, Tenant), CancellationToken.None);

        var command = await db.Commands.IgnoreQueryFilters().SingleAsync();

        command.Status.Should().Be(CommandStatus.RetryScheduled);

        // THE criterion. BeginSending took the attempt before anybody could know the connection
        // was in batch mode, so the deferral has to give it back — zero, not one.
        command.Attempts.Should().Be(
            0, "waiting for a scheduled hour is not an attempt at the external system");

        // Parked on the cut-off itself, not on the retry curve's next point (which the stub
        // policy puts at 10:00). An hour-later wake-up would simply ask again and defer again.
        command.NextAttemptAt.Should().Be(Cycle);

        command.LastErrorFamily.Should().Be(ErrorFamily.Transient);
        command.LastErrorMessage.Should().Contain(IntegrationErrors.BatchCycleNotDue);

        // The generator prefixes the instant in round-trippable form for the handler to parse;
        // what is STORED is the operator-facing half, with the hour named in words.
        command.LastErrorMessage.Should().Contain("2026-03-11 18:00:00Z");
        command.LastErrorMessage.Should().NotContain(
            "|", "the machine prefix is consumed by the handler, not shown to an operator");
    }

    [Fact]
    public async Task A_whole_day_of_dispatches_never_exhausts_the_budget()
    {
        using var factory = new TestIntegrationDbContextFactory(Tenant);
        var commandId = Guid.NewGuid();
        await SeedAsync(factory, commandId);

        await using var db = factory.CreateContext();

        // Twelve passes against a budget of eight: the dispatcher runs every minute, so between
        // 09:00 and the cut-off a real command is offered hundreds of times. Under the defect the
        // ninth pass rejected it.
        for (var pass = 0; pass < 12; pass++)
        {
            await Dispatch(db, Morning).Handle(
                new ExecuteIntegrationCommandCommand(commandId, Tenant), CancellationToken.None);
        }

        var command = await db.Commands.IgnoreQueryFilters().SingleAsync();

        command.Status.Should().Be(CommandStatus.RetryScheduled);
        command.Attempts.Should().Be(0);
        command.Status.Should().NotBe(
            CommandStatus.Rejected, "the ninth pass is where the defect rejected it");
    }

    [Fact]
    public async Task The_deferred_command_leaves_with_the_file_written_at_its_cut_off()
    {
        using var factory = new TestIntegrationDbContextFactory(Tenant);
        var commandId = Guid.NewGuid();
        await SeedAsync(factory, commandId);

        await using (var db = factory.CreateContext())
        {
            // A morning of dispatches and not one, because one is not what happens: the
            // dispatcher runs every minute. Under the defect the command survives a single pass
            // (it is merely RetryScheduled) and is lost on the ninth — so a one-pass version of
            // this test would pass against the broken code and prove nothing.
            for (var pass = 0; pass < 12; pass++)
            {
                await Dispatch(db, Morning).Handle(
                    new ExecuteIntegrationCommandCommand(commandId, Tenant), CancellationToken.None);
            }
        }

        // The scheduled generation, five minutes past the cut-off, with no seed — exactly what
        // OutboundBatchJob does. This is the assertion the status alone could not make: a command
        // that is Rejected, or parked in a state the eligible set excludes, is silently absent
        // from this file.
        await using var atCutOff = factory.CreateContext();
        var connection = await atCutOff.Connections.IgnoreQueryFilters().SingleAsync();

        var generated = await OutboundBatchTestContext
            .Generator(atCutOff, OutboundBatchTestContext.AfterCutOff)
            .GenerateAsync(Tenant, connection, seedCommandId: null, CancellationToken.None);

        generated.IsSuccess.Should().BeTrue();
        generated.Value.Generated.Should().BeTrue();
        generated.Value.RecordCount.Should().Be(1);

        var command = await atCutOff.Commands.IgnoreQueryFilters().SingleAsync();
        command.Status.Should().Be(CommandStatus.Batched);
        command.BatchFileId.Should().Be(generated.Value.FileId);
    }

    [Fact]
    public async Task A_detail_without_the_instant_falls_back_to_the_retry_budget()
    {
        // A defensive path, pinned because its FAILURE MODE is the point. If a malformed detail
        // were deferred anyway — to "now", say — the dispatcher would re-offer the command
        // immediately and spin for ever with no budget to stop it. Falling back to the ordinary
        // retry curve spends attempts and ends in a visible rejection, which is the right outcome
        // for a bug in the generator's own contract.
        await AssertFallsBackToRetry(
            IntegrationResult.Transient<Guid>(
                IntegrationErrors.BatchCycleNotDue, "no instant in front of this message"));
    }

    [Fact]
    public async Task A_cut_off_already_in_the_past_falls_back_to_the_retry_budget()
    {
        await AssertFallsBackToRetry(
            IntegrationResult.Transient<Guid>(
                IntegrationErrors.BatchCycleNotDue,
                $"{Morning.AddHours(-1):O}|an instant that has already passed"));
    }

    private static async Task AssertFallsBackToRetry(IntegrationResult<Guid> answer)
    {
        using var factory = new TestIntegrationDbContextFactory(Tenant);
        var commandId = Guid.NewGuid();
        await SeedAsync(factory, commandId);

        await using var db = factory.CreateContext();

        var handler = CommandsTestHarness.ExecuteHandler(
            db, new FakeAdapter(), Tenant, Customer,
            batch: new FixedAnswerEnlister(answer),
            clock: new OutboundBatchTestContext.FixedClock(Morning));

        await handler.Handle(
            new ExecuteIntegrationCommandCommand(commandId, Tenant), CancellationToken.None);

        var command = await db.Commands.IgnoreQueryFilters().SingleAsync();

        command.Status.Should().Be(CommandStatus.RetryScheduled);
        command.Attempts.Should().Be(1, "the claim's attempt stands when the deferral is refused");

        // CommandsTestHarness.Retry puts every retry one hour out, so this is the retry curve and
        // not the cut-off.
        command.NextAttemptAt.Should().Be(CommandsTestHarness.Now.AddHours(1));
    }

    private static async Task SeedAsync(TestIntegrationDbContextFactory factory, Guid commandId)
    {
        await using var seed = factory.CreateContext();

        seed.Connections.Add(OutboundBatchTestContext.BatchConnection(
            Tenant, Morning, id: ConnectionId));

        // Created in the morning: inside the day whose cut-off is still ahead, which is the whole
        // population this defect affected.
        seed.Commands.Add(OutboundBatchTestContext.Queued(
            Tenant, ConnectionId, Morning, crmId: Customer, id: commandId));

        await seed.SaveChangesAsync();
    }

    private static ExecuteIntegrationCommandHandler Dispatch(IntegrationDbContext db, DateTimeOffset now)
        => CommandsTestHarness.ExecuteHandler(
            db, new FakeAdapter(), Tenant, Customer,
            batch: new OutboundBatchFileEnlister(
                OutboundBatchTestContext.Generator(db, now),
                NullLogger<OutboundBatchFileEnlister>.Instance),
            clock: new OutboundBatchTestContext.FixedClock(now));

    /// <summary>
    /// An enlister that always answers the same thing, so the handler's branch can be exercised
    /// on details the real generator never produces.
    /// </summary>
    private sealed class FixedAnswerEnlister(IntegrationResult<Guid> answer) : IBatchFileEnlister
    {
        public Task<IntegrationResult<Guid>> EnlistAsync(
            IntegrationCommand command, IntegrationConnection connection, CancellationToken ct)
            => Task.FromResult(answer);
    }
}
