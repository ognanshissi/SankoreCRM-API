namespace Sankore.Modules.Integration.Tests.Features.Dispatch;

using FluentAssertions;
using Hangfire;
using Hangfire.Common;
using Hangfire.States;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Sankore.Modules.Integration.Domain;
using Sankore.Modules.Integration.Features.Dispatch;
using Sankore.Shared.Kernel;
using Xunit;

/// <summary>
/// INT-06 criterion 1: one job per tenant that actually owes work, and nothing else.
///
/// <para>
/// <c>IBackgroundJobClient.Enqueue&lt;T&gt;</c> is an extension method, so the substitute is
/// asserted on what it really calls — <c>Create(Job, IState)</c> — which also lets the test read
/// the serialised arguments back and check they are opaque.
/// </para>
/// </summary>
public sealed class IntegrationDispatchOrchestratorJobTests
{
    private static readonly Guid TenantWithWork = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001");
    private static readonly Guid TenantWithoutWork = Guid.Parse("bbbbbbbb-0000-0000-0000-000000000002");

    [Fact]
    public async Task Fans_out_one_job_per_tenant_owing_work_and_skips_the_others()
    {
        var databaseName = Guid.NewGuid().ToString();

        await using (var seed = DispatchTestContext.NewDb(TenantWithWork, databaseName))
        {
            seed.Commands.Add(DispatchTestContext.Command(
                TenantWithWork, Guid.NewGuid(), DispatchTestContext.Now.AddMinutes(-5)));
            await seed.SaveChangesAsync();
        }

        var hangfire = Substitute.For<IBackgroundJobClient>();
        var job = Build(databaseName, hangfire, TenantWithWork, TenantWithoutWork);

        await job.ExecuteAsync();

        var enqueued = EnqueuedTenantIds(hangfire);

        enqueued.Should().BeEquivalentTo([TenantWithWork]);
    }

    [Fact]
    public async Task Skips_the_system_placeholder_tenant()
    {
        // Guid.Empty is what every background context carries when it acts for the platform
        // rather than for a customer. Enqueuing a sweep for it would mean dispatching writes with
        // no owner, so the orchestrator drops it from the tenant list before anything else.
        var hangfire = Substitute.For<IBackgroundJobClient>();
        var job = Build(Guid.NewGuid().ToString(), hangfire, Guid.Empty);

        await job.ExecuteAsync();

        hangfire.DidNotReceiveWithAnyArgs().Create(default!, default!);
    }

    [Fact]
    public void A_command_cannot_be_attributed_to_the_system_placeholder_tenant_at_all()
    {
        // The stronger half of the guarantee above, and the reason the test before it seeds no
        // command: the aggregate refuses the placeholder outright, so a row the orchestrator
        // would have to skip cannot be created through the domain in the first place. The
        // orchestrator's filter is defence in depth against a row inserted by other means.
        var create = () => DispatchTestContext.Command(
            Guid.Empty, Guid.NewGuid(), DispatchTestContext.Now.AddMinutes(-5));

        create.Should().Throw<DomainException>().WithMessage("*TenantId*");
    }

    [Fact]
    public async Task Enqueues_nothing_when_no_tenant_owes_work()
    {
        var hangfire = Substitute.For<IBackgroundJobClient>();
        var job = Build(Guid.NewGuid().ToString(), hangfire, TenantWithWork, TenantWithoutWork);

        await job.ExecuteAsync();

        hangfire.DidNotReceiveWithAnyArgs().Create(default!, default!);
    }

    [Fact]
    public async Task Carries_only_an_opaque_tenant_identifier_into_the_queue()
    {
        var databaseName = Guid.NewGuid().ToString();

        await using (var seed = DispatchTestContext.NewDb(TenantWithWork, databaseName))
        {
            seed.Commands.Add(DispatchTestContext.Command(
                TenantWithWork, Guid.NewGuid(), DispatchTestContext.Now));
            await seed.SaveChangesAsync();
        }

        var hangfire = Substitute.For<IBackgroundJobClient>();
        var job = Build(databaseName, hangfire, TenantWithWork);

        await job.ExecuteAsync();

        var created = hangfire.ReceivedCalls()
            .Select(c => c.GetArguments()[0])
            .OfType<Job>()
            .Single();

        created.Type.Should().Be<DispatchTenantCommandsJob>();

        // The Hangfire tables are a shared queue and a dashboard page. One Guid and nothing else:
        // no payload, no customer name, no tenant record.
        created.Args.Should().HaveCount(1);
        created.Args[0].Should().Be(TenantWithWork);
    }

    [Fact]
    public async Task A_retry_scheduled_command_counts_as_work_only_once_it_is_due()
    {
        var databaseName = Guid.NewGuid().ToString();

        await using (var seed = DispatchTestContext.NewDb(TenantWithWork, databaseName))
        {
            var command = DispatchTestContext.Command(
                TenantWithWork, Guid.NewGuid(), DispatchTestContext.Now.AddHours(-1));

            command.BeginSending(new DispatchTestContext.FixedClock(DispatchTestContext.Now));
            command.ScheduleRetry(
                DispatchTestContext.Now.AddMinutes(30), "INTEGRATION_TIMEOUT", null,
                new DispatchTestContext.FixedClock(DispatchTestContext.Now));

            seed.Commands.Add(command);
            await seed.SaveChangesAsync();
        }

        var hangfire = Substitute.For<IBackgroundJobClient>();
        var job = Build(databaseName, hangfire, TenantWithWork);

        await job.ExecuteAsync();

        // The frozen clock sits half an hour before NextAttemptAt: the command exists, it is just
        // not owed yet, and the orchestrator must not wake a worker for it.
        hangfire.DidNotReceiveWithAnyArgs().Create(default!, default!);
    }

    [Fact]
    public async Task Owes_work_reads_only_the_tenant_it_was_asked_about()
    {
        // The predicate pairs IgnoreQueryFilters with an explicit tenant id. Without the explicit
        // half, a job running under one tenant's ambient context would answer for another's queue.
        await using var db = DispatchTestContext.NewDb(TenantWithoutWork);

        db.Commands.Add(DispatchTestContext.Command(
            TenantWithWork, Guid.NewGuid(), DispatchTestContext.Now));
        await db.SaveChangesAsync();

        var forOwner = await IntegrationDispatchOrchestratorJob.OwesWorkAsync(
            db, DispatchTestContext.Now, TenantWithWork, CancellationToken.None);

        var forOther = await IntegrationDispatchOrchestratorJob.OwesWorkAsync(
            db, DispatchTestContext.Now, TenantWithoutWork, CancellationToken.None);

        forOwner.Should().BeTrue();
        forOther.Should().BeFalse();
    }

    private static IntegrationDispatchOrchestratorJob Build(
        string databaseName, IBackgroundJobClient hangfire, params Guid[] activeTenants)
    {
        var provider = DispatchTestContext.NewProvider(databaseName);

        var tenantStore = Substitute.For<ITenantStore>();
        tenantStore.GetAllActiveAsync(Arg.Any<CancellationToken>())
            .Returns(activeTenants.Select(Tenant).ToList());

        return new IntegrationDispatchOrchestratorJob(
            provider.GetRequiredService<IServiceScopeFactory>(),
            tenantStore,
            hangfire,
            NullLogger<IntegrationDispatchOrchestratorJob>.Instance);
    }

    private static TenantInfo Tenant(Guid id) => new(
        Id: id,
        Name: "Test tenant",
        Fqdn: $"{id:N}.sankore.test",
        IsActive: true,
        IsMaintenance: false,
        TrialExpiresAt: null,
        BlockedAt: null);

    private static IReadOnlyList<Guid> EnqueuedTenantIds(IBackgroundJobClient hangfire)
        => hangfire.ReceivedCalls()
            .Select(c => c.GetArguments()[0])
            .OfType<Job>()
            .Where(j => j.Type == typeof(DispatchTenantCommandsJob))
            .Select(j => (Guid)j.Args[0]!)
            .ToList();
}
