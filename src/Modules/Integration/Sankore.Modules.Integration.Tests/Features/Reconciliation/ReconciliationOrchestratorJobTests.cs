namespace Sankore.Modules.Integration.Tests.Features.Reconciliation;

using FluentAssertions;
using Hangfire;
using Hangfire.Common;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Sankore.Modules.Integration.Features.Reconciliation;
using Sankore.Shared.Kernel;
using Xunit;

/// <summary>
/// INT-34's fan-out: one comparison per tenant that has something to compare, carrying nothing
/// but an opaque tenant identifier.
///
/// <para>
/// <c>IBackgroundJobClient.Enqueue&lt;T&gt;</c> is an extension method, so the substitute is
/// asserted on what it really calls — <c>Create(Job, IState)</c> — which also lets the test read
/// the serialised arguments back.
/// </para>
/// </summary>
public sealed class ReconciliationOrchestratorJobTests
{
    private static readonly Guid TenantWithReferences = ReconciliationTestContext.Tenant;
    private static readonly Guid TenantWithout = ReconciliationTestContext.OtherTenant;

    [Fact]
    public async Task Fans_out_one_comparison_per_tenant_holding_customer_references()
    {
        var databaseName = await SeedOneReference();

        var hangfire = Substitute.For<IBackgroundJobClient>();
        var job = Build(databaseName, hangfire, TenantWithReferences, TenantWithout);

        await job.ExecuteAsync();

        EnqueuedTenantIds(hangfire).Should().BeEquivalentTo([TenantWithReferences]);
    }

    [Fact]
    public async Task Enqueues_nothing_when_no_tenant_holds_a_reference()
    {
        // On a platform where a handful of two hundred tenants run a core banking system, fanning
        // out unconditionally would open a run row a night for each of the rest and read nothing.
        var hangfire = Substitute.For<IBackgroundJobClient>();
        var job = Build(Guid.NewGuid().ToString(), hangfire, TenantWithReferences, TenantWithout);

        await job.ExecuteAsync();

        hangfire.DidNotReceiveWithAnyArgs().Create(default!, default!);
    }

    [Fact]
    public async Task Skips_the_system_placeholder_tenant()
    {
        // Guid.Empty is what every background context carries when it acts for the platform
        // rather than for a customer. It owns no reference and no connection.
        var hangfire = Substitute.For<IBackgroundJobClient>();
        var job = Build(Guid.NewGuid().ToString(), hangfire, Guid.Empty);

        await job.ExecuteAsync();

        hangfire.DidNotReceiveWithAnyArgs().Create(default!, default!);
    }

    [Fact]
    public async Task Carries_only_an_opaque_tenant_identifier_into_the_queue()
    {
        var databaseName = await SeedOneReference();

        var hangfire = Substitute.For<IBackgroundJobClient>();
        var job = Build(databaseName, hangfire, TenantWithReferences);

        await job.ExecuteAsync();

        var created = hangfire.ReceivedCalls()
            .Select(c => c.GetArguments()[0])
            .OfType<Job>()
            .Single();

        created.Type.Should().Be<ReconcileTenantJob>();

        // The Hangfire tables are a shared queue and a dashboard page. One Guid and nothing else:
        // no customer reference, no external identifier, no tenant record.
        created.Args.Should().HaveCount(1);
        created.Args[0].Should().Be(TenantWithReferences);
    }

    [Fact]
    public async Task One_tenants_failure_does_not_stop_the_others()
    {
        // The per-tenant comparisons are already isolated from each other by Hangfire, so the
        // fan-out loop is the only place that can lose the whole platform's night.
        var databaseName = await SeedOneReference();

        var hangfire = Substitute.For<IBackgroundJobClient>();

        var tenantStore = Substitute.For<ITenantStore>();
        tenantStore.GetAllActiveAsync(Arg.Any<CancellationToken>()).Returns(
        [
            ReconciliationTestContext.TenantInfo(FailingTenant),
            ReconciliationTestContext.TenantInfo(TenantWithReferences),
        ]);

        var job = new ReconciliationOrchestratorJob(
            new FailingScopeFactory(
                ReconciliationTestContext.NewProvider(databaseName), FailingTenant),
            tenantStore,
            hangfire,
            NullLogger<ReconciliationOrchestratorJob>.Instance);

        await job.ExecuteAsync();

        EnqueuedTenantIds(hangfire).Should().BeEquivalentTo([TenantWithReferences]);
    }

    [Fact]
    public async Task The_reference_predicate_reads_only_the_tenant_it_was_asked_about()
    {
        await using var db = ReconciliationTestContext.NewDb(
            TenantWithout, Guid.NewGuid().ToString());

        db.References.Add(ReconciliationTestContext.Reference(
            TenantWithReferences, Guid.NewGuid(), "EXT-001"));
        await db.SaveChangesAsync();

        var forOwner = await ReconciliationOrchestratorJob.HasReferencesAsync(
            db, TenantWithReferences, CancellationToken.None);

        var forOther = await ReconciliationOrchestratorJob.HasReferencesAsync(
            db, TenantWithout, CancellationToken.None);

        forOwner.Should().BeTrue();
        forOther.Should().BeFalse();
    }

    [Fact]
    public void The_recurring_job_id_carries_nothing_hangfire_cannot_lock_on()
    {
        // Hangfire derives distributed lock names from the recurring-job id; a dot, a colon or a
        // URL in it breaks them (repo pitfall). Kebab-case only.
        ReconciliationOrchestratorJob.RecurringJobId.Should().MatchRegex("^[a-z0-9-]+$");
    }

    [Fact]
    public void The_comparison_runs_after_every_other_nightly_sweep()
    {
        // 06:00, chosen against the other nightly schedules rather than picked: M02's KYC review
        // at 01:00 moves tiers, M01's duplicate detection at 02:00 feeds the merges, its segment
        // and loyalty sweeps run at 03:00 and 03:30, and INT-22's ceiling watch reads the same two
        // sides at 05:00. Reading any of them mid-flight compares against state that is being
        // changed. If this hour is ever brought forward, the comparison starts reporting
        // divergences the night was still in the middle of resolving.
        ReconciliationOrchestratorJob.CronExpression.Should().Be("0 6 * * *");
    }

    [Fact]
    public void The_per_tenant_job_runs_on_a_queue_the_host_actually_declares()
    {
        // Setting BackgroundJobServerOptions.Queues REPLACES the default set, so a job whose queue
        // name is absent from the host's list is enqueued successfully and never processed, with
        // nothing in the logs. DispatchServiceRegistration.Queues is the module's single source
        // for that list, and this is what keeps the attribute and the host from drifting apart.
        Sankore.Modules.Integration.Features.Dispatch.DispatchServiceRegistration.Queues
            .Should().Contain(ReconcileTenantJob.QueueName);
    }

    private static readonly Guid FailingTenant = new("dddddddd-0000-0000-0000-000000000004");

    private static async Task<string> SeedOneReference()
    {
        var databaseName = Guid.NewGuid().ToString();

        await using var seed = ReconciliationTestContext.NewDb(TenantWithReferences, databaseName);

        seed.References.Add(ReconciliationTestContext.Reference(
            TenantWithReferences, Guid.NewGuid(), "EXT-001"));

        await seed.SaveChangesAsync();

        return databaseName;
    }

    private static ReconciliationOrchestratorJob Build(
        string databaseName, IBackgroundJobClient hangfire, params Guid[] activeTenants)
    {
        var provider = ReconciliationTestContext.NewProvider(databaseName);

        var tenantStore = Substitute.For<ITenantStore>();
        tenantStore.GetAllActiveAsync(Arg.Any<CancellationToken>())
            .Returns(activeTenants.Select(ReconciliationTestContext.TenantInfo).ToList());

        return new ReconciliationOrchestratorJob(
            provider.GetRequiredService<IServiceScopeFactory>(),
            tenantStore,
            hangfire,
            NullLogger<ReconciliationOrchestratorJob>.Instance);
    }

    private static IReadOnlyList<Guid> EnqueuedTenantIds(IBackgroundJobClient hangfire)
        => [.. hangfire.ReceivedCalls()
            .Select(c => c.GetArguments()[0])
            .OfType<Job>()
            .Where(j => j.Type == typeof(ReconcileTenantJob))
            .Select(j => (Guid)j.Args[0]!)];

    /// <summary>
    /// Throws when the scope is created for one particular tenant and behaves normally for every
    /// other. The ambient background context is what identifies the tenant, which is exactly the
    /// ordering the orchestrator establishes before creating its scope.
    /// </summary>
    private sealed class FailingScopeFactory(
        IServiceProvider inner, Guid failingTenantId) : IServiceScopeFactory
    {
        public IServiceScope CreateScope()
        {
            if (Sankore.Shared.Infrastructure.BackgroundJobs.BackgroundJobContext.CurrentTenant
                    ?.CurrentTenantId == failingTenantId)
            {
                throw new InvalidOperationException("Store unavailable for this tenant.");
            }

            return inner.GetRequiredService<IServiceScopeFactory>().CreateScope();
        }
    }
}
