namespace Sankore.Modules.Integration.Tests.Features.KycLimits;

using FluentAssertions;
using Hangfire;
using Hangfire.Common;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Sankore.Modules.Integration.Features.KycLimits;
using Sankore.Shared.Kernel;
using Xunit;

/// <summary>
/// INT-22's fan-out: one watch per tenant that has a snapshot to compare, carrying nothing but an
/// opaque tenant identifier.
///
/// <para>
/// <c>IBackgroundJobClient.Enqueue&lt;T&gt;</c> is an extension method, so the substitute is
/// asserted on what it really calls — <c>Create(Job, IState)</c> — which also lets the test read
/// the serialised arguments back.
/// </para>
/// </summary>
public sealed class KycLimitWatchOrchestratorJobTests
{
    private static readonly Guid TenantWithSnapshots = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001");
    private static readonly Guid TenantWithoutSnapshots = Guid.Parse("bbbbbbbb-0000-0000-0000-000000000002");

    [Fact]
    public async Task Fans_out_one_watch_per_tenant_having_a_snapshot()
    {
        var databaseName = Guid.NewGuid().ToString();

        await using (var seed = KycLimitsTestContext.NewDb(TenantWithSnapshots, databaseName))
        {
            seed.CbsSnapshots.Add(KycLimitsTestContext.Snapshot(
                TenantWithSnapshots, Guid.NewGuid(), 1_000m, 1_000m));
            await seed.SaveChangesAsync();
        }

        var hangfire = Substitute.For<IBackgroundJobClient>();
        var job = Build(databaseName, hangfire, TenantWithSnapshots, TenantWithoutSnapshots);

        await job.ExecuteAsync();

        EnqueuedTenantIds(hangfire).Should().BeEquivalentTo([TenantWithSnapshots]);
    }

    [Fact]
    public async Task Enqueues_nothing_when_no_tenant_has_a_snapshot()
    {
        // The watch reads cbs_customer_snapshot, which only exists for tenants whose CBS
        // connection has actually synced. On a platform where three tenants of two hundred use a
        // core banking system, fanning out unconditionally would schedule 197 jobs a night that
        // read nothing.
        var hangfire = Substitute.For<IBackgroundJobClient>();
        var job = Build(Guid.NewGuid().ToString(), hangfire, TenantWithSnapshots, TenantWithoutSnapshots);

        await job.ExecuteAsync();

        hangfire.DidNotReceiveWithAnyArgs().Create(default!, default!);
    }

    [Fact]
    public async Task Skips_the_system_placeholder_tenant()
    {
        // Guid.Empty is what every background context carries when it acts for the platform
        // rather than for a customer. It owns no snapshot and no KYC file.
        var hangfire = Substitute.For<IBackgroundJobClient>();
        var job = Build(Guid.NewGuid().ToString(), hangfire, Guid.Empty);

        await job.ExecuteAsync();

        hangfire.DidNotReceiveWithAnyArgs().Create(default!, default!);
    }

    [Fact]
    public async Task Carries_only_an_opaque_tenant_identifier_into_the_queue()
    {
        var databaseName = Guid.NewGuid().ToString();

        await using (var seed = KycLimitsTestContext.NewDb(TenantWithSnapshots, databaseName))
        {
            seed.CbsSnapshots.Add(KycLimitsTestContext.Snapshot(
                TenantWithSnapshots, Guid.NewGuid(), 1_000m, 1_000m));
            await seed.SaveChangesAsync();
        }

        var hangfire = Substitute.For<IBackgroundJobClient>();
        var job = Build(databaseName, hangfire, TenantWithSnapshots);

        await job.ExecuteAsync();

        var created = hangfire.ReceivedCalls()
            .Select(c => c.GetArguments()[0])
            .OfType<Job>()
            .Single();

        created.Type.Should().Be<KycLimitWatchJob>();

        // The Hangfire tables are a shared queue and a dashboard page. One Guid and nothing else:
        // no balance, no customer reference, no tenant record.
        created.Args.Should().HaveCount(1);
        created.Args[0].Should().Be(TenantWithSnapshots);
    }

    [Fact]
    public async Task One_tenants_failure_does_not_stop_the_others()
    {
        // The per-tenant watches are already isolated from each other by Hangfire, so the fan-out
        // loop is the only place that can lose the whole platform's night. A tenant whose store
        // refuses to answer is logged and the loop carries on.
        var databaseName = Guid.NewGuid().ToString();

        await using (var seed = KycLimitsTestContext.NewDb(TenantWithSnapshots, databaseName))
        {
            seed.CbsSnapshots.Add(KycLimitsTestContext.Snapshot(
                TenantWithSnapshots, Guid.NewGuid(), 1_000m, 1_000m));
            await seed.SaveChangesAsync();
        }

        var hangfire = Substitute.For<IBackgroundJobClient>();

        var tenantStore = Substitute.For<ITenantStore>();
        tenantStore.GetAllActiveAsync(Arg.Any<CancellationToken>()).Returns(
        [
            // A tenant whose own id is what the scope is built from: the provider below throws
            // for this one when the scope resolves its DbContext.
            KycLimitsTestContext.Tenant(FailingTenant),
            KycLimitsTestContext.Tenant(TenantWithSnapshots),
        ]);

        var job = new KycLimitWatchOrchestratorJob(
            new FailingScopeFactory(KycLimitsTestContext.NewProvider(databaseName), FailingTenant),
            tenantStore,
            hangfire,
            NullLogger<KycLimitWatchOrchestratorJob>.Instance);

        await job.ExecuteAsync();

        EnqueuedTenantIds(hangfire).Should().BeEquivalentTo([TenantWithSnapshots]);
    }

    [Fact]
    public async Task The_snapshot_predicate_reads_only_the_tenant_it_was_asked_about()
    {
        await using var db = KycLimitsTestContext.NewDb(
            TenantWithoutSnapshots, Guid.NewGuid().ToString());

        db.CbsSnapshots.Add(KycLimitsTestContext.Snapshot(
            TenantWithSnapshots, Guid.NewGuid(), 1_000m, 1_000m));
        await db.SaveChangesAsync();

        var forOwner = await KycLimitWatchOrchestratorJob.HasSnapshotsAsync(
            db, TenantWithSnapshots, CancellationToken.None);

        var forOther = await KycLimitWatchOrchestratorJob.HasSnapshotsAsync(
            db, TenantWithoutSnapshots, CancellationToken.None);

        forOwner.Should().BeTrue();
        forOther.Should().BeFalse();
    }

    [Fact]
    public void The_recurring_job_id_carries_nothing_hangfire_cannot_lock_on()
    {
        // Hangfire derives distributed lock names from the recurring-job id; a dot, a colon or a
        // URL in it breaks them (repo pitfall). Kebab-case only.
        KycLimitWatchOrchestratorJob.RecurringJobId.Should().MatchRegex("^[a-z0-9-]+$");
    }

    private static readonly Guid FailingTenant = Guid.Parse("dddddddd-0000-0000-0000-000000000004");

    private static KycLimitWatchOrchestratorJob Build(
        string databaseName, IBackgroundJobClient hangfire, params Guid[] activeTenants)
    {
        var provider = KycLimitsTestContext.NewProvider(databaseName);

        var tenantStore = Substitute.For<ITenantStore>();
        tenantStore.GetAllActiveAsync(Arg.Any<CancellationToken>())
            .Returns(activeTenants.Select(KycLimitsTestContext.Tenant).ToList());

        return new KycLimitWatchOrchestratorJob(
            provider.GetRequiredService<IServiceScopeFactory>(),
            tenantStore,
            hangfire,
            NullLogger<KycLimitWatchOrchestratorJob>.Instance);
    }

    private static IReadOnlyList<Guid> EnqueuedTenantIds(IBackgroundJobClient hangfire)
        => [.. hangfire.ReceivedCalls()
            .Select(c => c.GetArguments()[0])
            .OfType<Job>()
            .Where(j => j.Type == typeof(KycLimitWatchJob))
            .Select(j => (Guid)j.Args[0]!)];

    /// <summary>
    /// Throws when the scope is created for one particular tenant, and behaves normally for every
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
