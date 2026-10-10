namespace Sankore.Modules.Integration.Tests.Features.Dispatch;

using FluentAssertions;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Sankore.Modules.Integration.Domain;
using Sankore.Modules.Integration.Features.Commands;
using Sankore.Modules.Integration.Features.Commands.Execute;
using Sankore.Modules.Integration.Features.Dispatch;
using Sankore.Modules.Integration.Infrastructure;
using Sankore.Modules.Integration.PublicApi;
using Sankore.Shared.Infrastructure.Auth;
using Sankore.Shared.Infrastructure.BackgroundJobs;
using Sankore.Shared.Kernel;
using Xunit;

/// <summary>
/// INT-06 criteria 2, 3 and 4: the SYSTEM identity, the per-entity ordering, and the budget.
/// </summary>
public sealed class DispatchTenantCommandsJobTests
{
    private static readonly Guid Tenant = Guid.Parse("cccccccc-0000-0000-0000-000000000003");
    private static readonly Guid Customer = Guid.Parse("dddddddd-0000-0000-0000-000000000004");
    private static readonly Guid OtherCustomer = Guid.Parse("eeeeeeee-0000-0000-0000-000000000005");

    private static readonly Guid Older = Guid.Parse("00000000-0000-0000-0000-0000000000a1");
    private static readonly Guid Newer = Guid.Parse("00000000-0000-0000-0000-0000000000a2");

    private readonly DispatchTestContext.FixedClock _clock = new(DispatchTestContext.Now);

    // ── Criterion 3 — ordering ──────────────────────────────────────────────

    [Fact]
    public async Task One_customers_commands_execute_oldest_first()
    {
        await using var db = DispatchTestContext.NewDb(Tenant);

        // Deliberately inserted newest-first, so a job that simply walked the table would get it
        // wrong.
        db.Commands.Add(DispatchTestContext.Command(
            Tenant, Customer, DispatchTestContext.Now.AddMinutes(-5),
            type: CommandType.UpdateCustomer, id: Newer));
        db.Commands.Add(DispatchTestContext.Command(
            Tenant, Customer, DispatchTestContext.Now.AddMinutes(-30),
            type: CommandType.CreateCustomer, id: Older));
        await db.SaveChangesAsync();

        var sent = new List<Guid>();
        var sender = SenderThatSucceeds(sent);

        var report = await RunAsync(db, sender);

        sent.Should().Equal(Older, Newer);
        report.Succeeded.Should().Be(2);
        report.GroupsHalted.Should().Be(0);
    }

    [Fact]
    public async Task A_failure_on_the_first_command_does_not_let_the_second_overtake_it()
    {
        await using var db = DispatchTestContext.NewDb(Tenant);

        db.Commands.Add(DispatchTestContext.Command(
            Tenant, Customer, DispatchTestContext.Now.AddMinutes(-30),
            type: CommandType.CreateCustomer, id: Older));
        db.Commands.Add(DispatchTestContext.Command(
            Tenant, Customer, DispatchTestContext.Now.AddMinutes(-5),
            type: CommandType.UpdateCustomer, id: Newer));
        await db.SaveChangesAsync();

        var sent = new List<Guid>();
        var sender = Substitute.For<ISender>();
        sender
            .Send(Arg.Any<ExecuteIntegrationCommandCommand>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var request = (ExecuteIntegrationCommandCommand)call[0];
                sent.Add(request.CommandId);

                // The handler recorded a retry — which arrives as a SUCCESSFUL Result carrying the
                // status, because TransactionBehavior would otherwise roll back the row that
                // records it.
                return Task.FromResult(Outcome(request.CommandId, CommandStatus.RetryScheduled));
            });

        var report = await RunAsync(db, sender);

        // An UpdateCustomer reaching the CBS before the CreateCustomer it amends fails as "entity
        // not found". The second command must simply wait.
        sent.Should().Equal(Older);
        report.Rescheduled.Should().Be(1);
        report.GroupsHalted.Should().Be(1);
    }

    [Fact]
    public async Task A_rejection_also_halts_the_entitys_queue()
    {
        await using var db = DispatchTestContext.NewDb(Tenant);

        db.Commands.Add(DispatchTestContext.Command(
            Tenant, Customer, DispatchTestContext.Now.AddMinutes(-30), id: Older));
        db.Commands.Add(DispatchTestContext.Command(
            Tenant, Customer, DispatchTestContext.Now.AddMinutes(-5), id: Newer));
        await db.SaveChangesAsync();

        var sent = new List<Guid>();
        var sender = Substitute.For<ISender>();
        sender
            .Send(Arg.Any<ExecuteIntegrationCommandCommand>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var request = (ExecuteIntegrationCommandCommand)call[0];
                sent.Add(request.CommandId);
                return Task.FromResult(Outcome(request.CommandId, CommandStatus.Rejected));
            });

        var report = await RunAsync(db, sender);

        sent.Should().Equal(Older);
        report.Rejected.Should().Be(1);
    }

    [Fact]
    public async Task One_halted_customer_does_not_hold_up_another()
    {
        await using var db = DispatchTestContext.NewDb(Tenant);

        db.Commands.Add(DispatchTestContext.Command(
            Tenant, Customer, DispatchTestContext.Now.AddMinutes(-30), id: Older));
        db.Commands.Add(DispatchTestContext.Command(
            Tenant, Customer, DispatchTestContext.Now.AddMinutes(-5), id: Newer));
        db.Commands.Add(DispatchTestContext.Command(
            Tenant, OtherCustomer, DispatchTestContext.Now.AddMinutes(-20)));
        await db.SaveChangesAsync();

        var sender = Substitute.For<ISender>();
        sender
            .Send(Arg.Any<ExecuteIntegrationCommandCommand>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var request = (ExecuteIntegrationCommandCommand)call[0];

                // Only the first customer's oldest command throws. Everything else is fine.
                return request.CommandId == Older
                    ? throw new InvalidOperationException("adapter blew up")
                    : Task.FromResult(Outcome(request.CommandId, CommandStatus.Succeeded));
            });

        var report = await RunAsync(db, sender);

        report.Faulted.Should().Be(1);
        report.GroupsHalted.Should().Be(1);

        // The other customer's command went out regardless: a fault is scoped to its own entity
        // queue, never to the tenant.
        report.Succeeded.Should().Be(1);
    }

    [Fact]
    public async Task A_command_of_another_entity_type_is_its_own_queue()
    {
        await using var db = DispatchTestContext.NewDb(Tenant);

        // Same CrmId, different EntityType: an account and a customer that happen to share an
        // identifier are unrelated, and grouping on CrmId alone would serialise them.
        db.Commands.Add(DispatchTestContext.Command(
            Tenant, Customer, DispatchTestContext.Now.AddMinutes(-30),
            entityType: IntegrationEntityTypes.Customer, id: Older));
        db.Commands.Add(DispatchTestContext.Command(
            Tenant, Customer, DispatchTestContext.Now.AddMinutes(-20),
            entityType: IntegrationEntityTypes.Account, type: CommandType.OpenAccount, id: Newer));
        await db.SaveChangesAsync();

        var sent = new List<Guid>();
        var sender = Substitute.For<ISender>();
        sender
            .Send(Arg.Any<ExecuteIntegrationCommandCommand>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var request = (ExecuteIntegrationCommandCommand)call[0];
                sent.Add(request.CommandId);

                return request.CommandId == Older
                    ? Task.FromResult(Outcome(request.CommandId, CommandStatus.Rejected))
                    : Task.FromResult(Outcome(request.CommandId, CommandStatus.Succeeded));
            });

        await RunAsync(db, sender);

        sent.Should().Contain(Newer);
    }

    // ── Criterion 4 — the budget, on the one path the handler cannot cover ──

    [Fact]
    public async Task A_command_left_sending_by_a_faulted_attempt_is_rescheduled()
    {
        await using var db = DispatchTestContext.NewDb(Tenant);

        var command = DispatchTestContext.Command(
            Tenant, Customer, DispatchTestContext.Now.AddMinutes(-30), id: Older);

        // Claimed half an hour ago by a worker that then died. The claim was saved before any
        // call, so it survives — and because the process was KILLED, no catch block ran and no
        // verdict was written. The row is simply stranded in Sending, which is the state only an
        // expired claim can rescue: a fresh one is indistinguishable from a call in flight.
        command.BeginSending(new DispatchTestContext.FixedClock(
            DispatchTestContext.Now.AddMinutes(-30)));

        db.Commands.Add(command);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var sender = Substitute.For<ISender>();
        sender
            .Send(Arg.Any<ExecuteIntegrationCommandCommand>(), Arg.Any<CancellationToken>())
            .Returns<Task<Result<ExecuteIntegrationCommandResult>>>(
                _ => throw new TimeoutException("the worker died mid-call"));

        var report = await RunAsync(db, sender, retryPolicy: Policy(maxAttempts: 8));

        report.Recovered.Should().Be(1);
        report.Rescheduled.Should().Be(1);

        var stored = await db.Commands.IgnoreQueryFilters().AsNoTracking()
            .SingleAsync(c => c.Id == Older);

        // IsDueAt is false for Sending, so without this recovery the command would sit claimed
        // for ever — invisible to the rejection queue an administrator reads.
        stored.Status.Should().Be(CommandStatus.RetryScheduled);
        stored.NextAttemptAt.Should().NotBeNull();
    }

    [Fact]
    public async Task A_faulted_command_past_its_budget_is_rejected_as_transient()
    {
        await using var db = DispatchTestContext.NewDb(Tenant);

        var command = DispatchTestContext.Command(
            Tenant, Customer, DispatchTestContext.Now.AddHours(-3), id: Older);

        // Three attempts already consumed, against a budget of three. The last claim is stamped
        // in the past so it has expired: an attempt budget can only be adjudicated on a command
        // the sweep is allowed to pick up again.
        var died = new DispatchTestContext.FixedClock(DispatchTestContext.Now.AddMinutes(-30));

        command.BeginSending(_clock);
        command.ScheduleRetry(DispatchTestContext.Now.AddMinutes(-10), "INTEGRATION_TIMEOUT", null, _clock);
        command.BeginSending(_clock);
        command.ScheduleRetry(DispatchTestContext.Now.AddMinutes(-5), "INTEGRATION_TIMEOUT", null, _clock);
        command.BeginSending(died);

        db.Commands.Add(command);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var sender = Substitute.For<ISender>();
        sender
            .Send(Arg.Any<ExecuteIntegrationCommandCommand>(), Arg.Any<CancellationToken>())
            .Returns<Task<Result<ExecuteIntegrationCommandResult>>>(
                _ => throw new TimeoutException("still down"));

        var report = await RunAsync(db, sender, retryPolicy: Policy(maxAttempts: 3));

        report.Rejected.Should().Be(1);

        var stored = await db.Commands.IgnoreQueryFilters().AsNoTracking()
            .SingleAsync(c => c.Id == Older);

        stored.Status.Should().Be(CommandStatus.Rejected);

        // Transient even though the budget is what ran out: the family tells an administrator
        // whether retrying could ever have helped, and our own crash is exactly a thing that can
        // pass.
        stored.LastErrorFamily.Should().Be(ErrorFamily.Transient);
    }

    [Fact]
    public async Task A_command_left_pending_by_a_faulted_attempt_is_claimed_rather_than_hot_looped()
    {
        await using var db = DispatchTestContext.NewDb(Tenant);

        db.Commands.Add(DispatchTestContext.Command(
            Tenant, Customer, DispatchTestContext.Now.AddMinutes(-30), id: Older));
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var sender = Substitute.For<ISender>();
        sender
            .Send(Arg.Any<ExecuteIntegrationCommandCommand>(), Arg.Any<CancellationToken>())
            .Returns<Task<Result<ExecuteIntegrationCommandResult>>>(
                _ => throw new InvalidOperationException("validator refused before the claim"));

        await RunAsync(db, sender);

        var stored = await db.Commands.IgnoreQueryFilters().AsNoTracking()
            .SingleAsync(c => c.Id == Older);

        // Left Pending it would be re-sent every single minute for as long as the fault lasts.
        stored.Status.Should().Be(CommandStatus.RetryScheduled);
        stored.Attempts.Should().Be(1);
    }

    [Fact]
    public async Task A_verdict_the_handler_already_committed_is_left_alone()
    {
        await using var db = DispatchTestContext.NewDb(Tenant);

        var command = DispatchTestContext.Command(
            Tenant, Customer, DispatchTestContext.Now.AddMinutes(-30), id: Older);

        command.BeginSending(_clock);
        command.Reject(ErrorFamily.Functional, "INTEGRATION_DUPLICATE", "already a customer", _clock);

        db.Commands.Add(command);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        // Not due any more, so it is not even selected — but prove the recovery path would not
        // touch it either, by faulting on a command in the same state.
        var sender = Substitute.For<ISender>();

        var report = await RunAsync(db, sender);

        report.Attempted.Should().Be(0);
        await sender.DidNotReceiveWithAnyArgs()
            .Send(Arg.Any<ExecuteIntegrationCommandCommand>(), Arg.Any<CancellationToken>());

        var stored = await db.Commands.IgnoreQueryFilters().AsNoTracking()
            .SingleAsync(c => c.Id == Older);

        stored.LastErrorFamily.Should().Be(ErrorFamily.Functional);
    }

    // ── Criterion 2 — SYSTEM identity, opaque arguments ─────────────────────

    [Fact]
    public async Task Establishes_the_system_identity_before_the_scope_and_for_the_whole_send()
    {
        var databaseName = Guid.NewGuid().ToString();

        await using (var seed = DispatchTestContext.NewDb(Tenant, databaseName))
        {
            seed.Commands.Add(DispatchTestContext.Command(
                Tenant, Customer, DispatchTestContext.Now.AddMinutes(-5), id: Older));
            await seed.SaveChangesAsync();
        }

        ICurrentUser? userAtSendTime = null;
        Guid? tenantAtSendTime = null;
        ExecuteIntegrationCommandCommand? request = null;

        var sender = Substitute.For<ISender>();
        sender
            .Send(Arg.Any<ExecuteIntegrationCommandCommand>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                request = (ExecuteIntegrationCommandCommand)call[0];
                userAtSendTime = BackgroundJobContext.CurrentUser;
                tenantAtSendTime = BackgroundJobContext.CurrentTenant?.CurrentTenantId;

                return Task.FromResult(Outcome(request.CommandId, CommandStatus.Succeeded));
            });

        // ITenantContext is registered the way Program.cs registers it — ambient context first,
        // and it THROWS when there is none. Resolving IntegrationDbContext inside the job's scope
        // is therefore the assertion: a scope created before SetScope would have no ambient tenant
        // to build it from.
        var provider = DispatchTestContext.NewProvider(
            databaseName,
            s =>
            {
                s.AddSingleton(sender);
                s.AddSingleton<ICommandRetryPolicy>(Policy(8));
            });

        var job = new DispatchTenantCommandsJob(provider.GetRequiredService<IServiceScopeFactory>());

        await job.ExecuteAsync(Tenant);

        userAtSendTime.Should().BeOfType<SystemCurrentUser>();
        userAtSendTime!.DisplayName.Should().Be("SYSTEM");
        userAtSendTime.TenantId.Should().Be(Tenant);
        tenantAtSendTime.Should().Be(Tenant);

        // Opaque arguments: two identifiers, nothing a dashboard reader could mistake for
        // customer data.
        request.Should().NotBeNull();
        request!.CommandId.Should().Be(Older);
        request.TenantId.Should().Be(Tenant);
    }

    [Fact]
    public void Runs_on_the_integration_write_queue()
    {
        var queue = typeof(DispatchTenantCommandsJob)
            .GetCustomAttributes(typeof(Hangfire.QueueAttribute), inherit: false)
            .Cast<Hangfire.QueueAttribute>()
            .Single();

        queue.Queue.Should().Be("integration-write");
        DispatchTenantCommandsJob.QueueName.Should().Be("integration-write");
    }

    // ── Helpers ─────────────────────────────────────────────────────────────

    private Task<DispatchSweepReport> RunAsync(
        IntegrationDbContext db, ISender sender, ICommandRetryPolicy? retryPolicy = null)
        => DispatchTenantCommandsJob.RunAsync(
            db,
            sender,
            retryPolicy ?? Policy(maxAttempts: 8),
            _clock,
            NullLogger.Instance,
            Tenant,
            CancellationToken.None);

    private static ISender SenderThatSucceeds(List<Guid> sent)
    {
        var sender = Substitute.For<ISender>();
        sender
            .Send(Arg.Any<ExecuteIntegrationCommandCommand>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var request = (ExecuteIntegrationCommandCommand)call[0];
                sent.Add(request.CommandId);
                return Task.FromResult(Outcome(request.CommandId, CommandStatus.Succeeded));
            });

        return sender;
    }

    private static Result<ExecuteIntegrationCommandResult> Outcome(Guid id, CommandStatus status)
        => Result.Ok(new ExecuteIntegrationCommandResult(id, status.ToString(), null, null));

    private static ICommandRetryPolicy Policy(int maxAttempts)
    {
        var policy = Substitute.For<ICommandRetryPolicy>();
        policy.MaxAttempts.Returns(maxAttempts);
        policy.NextAttemptAt(Arg.Any<int>())
            .Returns(DispatchTestContext.Now.AddMinutes(30));

        return policy;
    }
}
