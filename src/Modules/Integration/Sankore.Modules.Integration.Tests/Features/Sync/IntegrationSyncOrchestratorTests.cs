namespace Sankore.Modules.Integration.Tests.Features.Sync;

using FluentAssertions;
using Hangfire;
using Hangfire.Common;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Sankore.Modules.Integration.Domain;
using Sankore.Modules.Integration.Features.Sync;
using Sankore.Modules.Integration.PublicApi;
using Sankore.Shared.Infrastructure.BackgroundJobs;
using Sankore.Shared.Kernel;
using Xunit;

/// <summary>
/// INT-20 criterion 1 — one job per active connection and per stream of its family, with a period
/// configurable per stream and per tenant — and criterion 5's tenant isolation.
///
/// <para>
/// <c>IBackgroundJobClient.Enqueue&lt;T&gt;</c> is an extension method, so the substitute is
/// asserted on what it really calls, <c>Create(Job, IState)</c>, which also lets the test read the
/// serialised arguments back and check they are opaque.
/// </para>
/// </summary>
public sealed class IntegrationSyncOrchestratorTests
{
    private static readonly Guid TenantA = Guid.Parse("aaaaaaaa-1111-0000-0000-000000000001");
    private static readonly Guid TenantB = Guid.Parse("bbbbbbbb-2222-0000-0000-000000000002");

    [Fact]
    public async Task Fans_out_one_job_per_active_connection_and_per_stream_of_its_family()
    {
        var databaseName = Guid.NewGuid().ToString();
        Guid coreBanking;
        Guid insurance;

        await using (var seed = SyncTestContext.NewDb(TenantA, databaseName))
        {
            coreBanking = SyncTestContext.SeedConnection(seed, TenantA).Id;
            insurance = SyncTestContext.SeedConnection(
                seed, TenantA, IntegrationFamily.Insurance, name: "Assureur").Id;
        }

        var hangfire = Substitute.For<IBackgroundJobClient>();

        await Build(databaseName, hangfire, TenantA).ExecuteAsync();

        // Four core banking streams, two insurance ones, and nothing crossing over: asking a core
        // banking system for Claims is not an empty answer, it is a call that cannot be made.
        Enqueued(hangfire).Should().BeEquivalentTo(
        [
            (coreBanking, SyncStream.Customers),
            (coreBanking, SyncStream.Accounts),
            (coreBanking, SyncStream.Transactions),
            (coreBanking, SyncStream.Loans),
            (insurance, SyncStream.Policies),
            (insurance, SyncStream.Claims),
        ]);
    }

    [Fact]
    public async Task Skips_an_inactive_connection()
    {
        var databaseName = Guid.NewGuid().ToString();

        await using (var seed = SyncTestContext.NewDb(TenantA, databaseName))
        {
            SyncTestContext.SeedConnection(seed, TenantA, active: false);
        }

        var hangfire = Substitute.For<IBackgroundJobClient>();

        await Build(databaseName, hangfire, TenantA).ExecuteAsync();

        hangfire.DidNotReceiveWithAnyArgs().Create(default!, default!);
    }

    [Fact]
    public async Task Skips_a_stream_whose_period_has_not_elapsed()
    {
        var databaseName = Guid.NewGuid().ToString();
        Guid connectionId;

        await using (var seed = SyncTestContext.NewDb(TenantA, databaseName))
        {
            connectionId = SyncTestContext.SeedConnection(seed, TenantA).Id;

            // Ten minutes ago, against defaults of 60 (Customers, Accounts) and 240
            // (Transactions, Loans): nothing is due.
            foreach (var stream in new[]
                     {
                         SyncStream.Customers, SyncStream.Accounts,
                         SyncStream.Transactions, SyncStream.Loans,
                     })
            {
                SyncTestContext.SeedCursor(
                    seed, TenantA, connectionId, stream, SyncTestContext.Now.AddMinutes(-10));
            }
        }

        var hangfire = Substitute.For<IBackgroundJobClient>();

        await Build(databaseName, hangfire, TenantA).ExecuteAsync();

        hangfire.DidNotReceiveWithAnyArgs().Create(default!, default!);
    }

    [Fact]
    public async Task A_stream_becomes_due_once_its_own_default_period_has_elapsed()
    {
        var databaseName = Guid.NewGuid().ToString();
        Guid connectionId;

        await using (var seed = SyncTestContext.NewDb(TenantA, databaseName))
        {
            connectionId = SyncTestContext.SeedConnection(seed, TenantA).Id;

            foreach (var stream in new[]
                     {
                         SyncStream.Customers, SyncStream.Accounts,
                         SyncStream.Transactions, SyncStream.Loans,
                     })
            {
                SyncTestContext.SeedCursor(
                    seed, TenantA, connectionId, stream, SyncTestContext.Now.AddMinutes(-61));
            }
        }

        var hangfire = Substitute.For<IBackgroundJobClient>();

        await Build(databaseName, hangfire, TenantA).ExecuteAsync();

        // The screen-facing streams default to an hour and are due; history defaults to four
        // hours and is not. The two defaults are not decoration — the figure a counter needs in
        // the second is INT-15's live balance, never this sweep.
        Enqueued(hangfire).Should().BeEquivalentTo(
        [
            (connectionId, SyncStream.Customers),
            (connectionId, SyncStream.Accounts),
        ]);
    }

    [Fact]
    public async Task A_cursor_that_has_never_run_is_due_immediately()
    {
        var databaseName = Guid.NewGuid().ToString();
        Guid connectionId;

        await using (var seed = SyncTestContext.NewDb(TenantA, databaseName))
        {
            connectionId = SyncTestContext.SeedConnection(seed, TenantA).Id;

            // A row exists but has never been begun. Null LastRunAt means due — a connection
            // configured a minute ago must not wait an hour for its first sweep.
            SyncTestContext.SeedCursor(seed, TenantA, connectionId, SyncStream.Customers);
        }

        var hangfire = Substitute.For<IBackgroundJobClient>();

        await Build(databaseName, hangfire, TenantA).ExecuteAsync();

        Enqueued(hangfire).Should().Contain((connectionId, SyncStream.Customers));
    }

    [Fact]
    public async Task Reads_the_period_from_the_connection_settings_and_falls_back_to_the_default()
    {
        var databaseName = Guid.NewGuid().ToString();
        Guid connectionId;

        await using (var seed = SyncTestContext.NewDb(TenantA, databaseName))
        {
            // Criterion 1's "configurable per stream and per tenant": the settings belong to one
            // connection, which belongs to exactly one tenant. Customers is tightened to a
            // quarter of an hour; the other three streams say nothing and keep their defaults.
            connectionId = SyncTestContext.SeedConnection(
                seed, TenantA,
                settings: SyncTestContext.Settings(
                    new Dictionary<SyncStream, int> { [SyncStream.Customers] = 15 })).Id;

            foreach (var stream in new[]
                     {
                         SyncStream.Customers, SyncStream.Accounts,
                         SyncStream.Transactions, SyncStream.Loans,
                     })
            {
                SyncTestContext.SeedCursor(
                    seed, TenantA, connectionId, stream, SyncTestContext.Now.AddMinutes(-20));
            }
        }

        var hangfire = Substitute.For<IBackgroundJobClient>();

        await Build(databaseName, hangfire, TenantA).ExecuteAsync();

        // Twenty minutes ago: past the configured fifteen, short of the default sixty. This also
        // pins that the per-stream dictionary survives the jsonb round trip — an enum-keyed map
        // that silently serialised to nothing would make every connection look unconfigured.
        Enqueued(hangfire).Should().BeEquivalentTo([(connectionId, SyncStream.Customers)]);
    }

    [Fact]
    public async Task A_stored_period_of_zero_falls_back_to_the_default_rather_than_running_continuously()
    {
        var databaseName = Guid.NewGuid().ToString();
        Guid connectionId;

        await using (var seed = SyncTestContext.NewDb(TenantA, databaseName))
        {
            connectionId = SyncTestContext.SeedConnection(
                seed, TenantA,
                settings: SyncTestContext.Settings(
                    new Dictionary<SyncStream, int> { [SyncStream.Customers] = 0 })).Id;

            SyncTestContext.SeedCursor(
                seed, TenantA, connectionId, SyncStream.Customers, SyncTestContext.Now.AddMinutes(-10));
        }

        var hangfire = Substitute.For<IBackgroundJobClient>();

        await Build(databaseName, hangfire, TenantA).ExecuteAsync();

        // A misconfigured zero must not turn this orchestrator into a hot loop against somebody's
        // production CBS. Ten minutes in, the default hour has not elapsed.
        Enqueued(hangfire).Should().NotContain((connectionId, SyncStream.Customers));
    }

    [Fact]
    public async Task Skips_the_system_placeholder_tenant()
    {
        // Guid.Empty is what every background context carries when it acts for the platform rather
        // than for a customer. It owns no connection, and a sweep for it would be a sweep with no
        // owner.
        var hangfire = Substitute.For<IBackgroundJobClient>();

        await Build(Guid.NewGuid().ToString(), hangfire, Guid.Empty).ExecuteAsync();

        hangfire.DidNotReceiveWithAnyArgs().Create(default!, default!);
    }

    [Fact]
    public async Task Carries_only_opaque_identifiers_into_the_queue()
    {
        var databaseName = Guid.NewGuid().ToString();
        Guid connectionId;

        await using (var seed = SyncTestContext.NewDb(TenantA, databaseName))
        {
            connectionId = SyncTestContext.SeedConnection(
                seed, TenantA,
                settings: SyncTestContext.Settings(
                    new Dictionary<SyncStream, int>
                    {
                        [SyncStream.Customers] = 1,
                        [SyncStream.Accounts] = 10_000,
                        [SyncStream.Transactions] = 10_000,
                        [SyncStream.Loans] = 10_000,
                    })).Id;

            // Every stream needs a cursor: a stream with none has never run, which means due, and
            // the point of this test is to look at exactly one enqueued job.
            foreach (var stream in new[]
                     {
                         SyncStream.Customers, SyncStream.Accounts,
                         SyncStream.Transactions, SyncStream.Loans,
                     })
            {
                SyncTestContext.SeedCursor(
                    seed, TenantA, connectionId, stream, SyncTestContext.Now.AddMinutes(-5));
            }
        }

        var hangfire = Substitute.For<IBackgroundJobClient>();

        await Build(databaseName, hangfire, TenantA).ExecuteAsync();

        var created = Jobs(hangfire).Single();

        created.Type.Should().Be<IntegrationSyncJob>();

        // The Hangfire tables are a shared queue and a dashboard page. Two Guids and a stream
        // name: no customer identifier, no settings object, no tenant record.
        created.Args.Should().HaveCount(3);
        created.Args[0].Should().Be(TenantA);
        created.Args[1].Should().Be(connectionId);
        created.Args[2].Should().Be(SyncStream.Customers);
    }

    [Fact]
    public async Task A_tenant_whose_fan_out_throws_does_not_stop_the_other_tenants()
    {
        // Criterion 5, first half. One tenant's unreadable state must cost that tenant its minute,
        // not everyone else's — and a recurring job that throws is only a red line in a dashboard
        // nobody is watching, so the loop has to survive it rather than report it.
        var databaseName = Guid.NewGuid().ToString();
        Guid connectionB;

        await using (var seed = SyncTestContext.NewDb(TenantA, databaseName))
        {
            SyncTestContext.SeedConnection(seed, TenantA);
            connectionB = SyncTestContext.SeedConnection(seed, TenantB, name: "CBS du tenant B").Id;
        }

        var hangfire = Substitute.For<IBackgroundJobClient>();
        var provider = SyncTestContext.NewProvider(databaseName);

        var tenantStore = Substitute.For<ITenantStore>();
        tenantStore.GetAllActiveAsync(Arg.Any<CancellationToken>())
            .Returns(new[] { TenantA, TenantB }.Select(Tenant).ToList());

        var orchestrator = new IntegrationSyncOrchestrator(
            // TenantA is listed first, so without the per-tenant guard its failure would abort the
            // loop before TenantB was ever examined.
            new FailingScopeFactory(
                provider.GetRequiredService<IServiceScopeFactory>(), failFor: TenantA),
            tenantStore,
            hangfire,
            NullLogger<IntegrationSyncOrchestrator>.Instance);

        await orchestrator.ExecuteAsync();

        Enqueued(hangfire).Should().OnlyContain(e => e.ConnectionId == connectionB);
        Enqueued(hangfire).Should().HaveCount(4);
    }

    [Fact]
    public async Task Reads_only_the_tenant_it_was_asked_about()
    {
        // The predicate pairs IgnoreQueryFilters with an explicit tenant id. Without the explicit
        // half, a job running under one tenant's ambient context would fan out another's
        // connections — and then sweep them under the wrong tenant.
        await using var db = SyncTestContext.NewDb(TenantB);

        var connectionA = SyncTestContext.SeedConnection(db, TenantA).Id;

        var forOwner = await IntegrationSyncOrchestrator.DueStreamsAsync(
            db, SyncTestContext.Now, TenantA, CancellationToken.None);

        var forOther = await IntegrationSyncOrchestrator.DueStreamsAsync(
            db, SyncTestContext.Now, TenantB, CancellationToken.None);

        forOwner.Should().OnlyContain(d => d.ConnectionId == connectionA).And.HaveCount(4);
        forOther.Should().BeEmpty();
    }

    private static IntegrationSyncOrchestrator Build(
        string databaseName, IBackgroundJobClient hangfire, params Guid[] activeTenants)
    {
        var provider = SyncTestContext.NewProvider(databaseName);

        var tenantStore = Substitute.For<ITenantStore>();
        tenantStore.GetAllActiveAsync(Arg.Any<CancellationToken>())
            .Returns(activeTenants.Select(Tenant).ToList());

        return new IntegrationSyncOrchestrator(
            provider.GetRequiredService<IServiceScopeFactory>(),
            tenantStore,
            hangfire,
            NullLogger<IntegrationSyncOrchestrator>.Instance);
    }

    private static TenantInfo Tenant(Guid id) => new(
        Id: id,
        Name: "Test tenant",
        Fqdn: $"{id:N}.sankore.test",
        IsActive: true,
        IsMaintenance: false,
        TrialExpiresAt: null,
        BlockedAt: null);

    private static IReadOnlyList<Job> Jobs(IBackgroundJobClient hangfire)
        => hangfire.ReceivedCalls()
            .Select(c => c.GetArguments()[0])
            .OfType<Job>()
            .Where(j => j.Type == typeof(IntegrationSyncJob))
            .ToList();

    private static IReadOnlyList<(Guid ConnectionId, SyncStream Stream)> Enqueued(
        IBackgroundJobClient hangfire)
        => Jobs(hangfire)
            .Select(j => ((Guid)j.Args[1]!, (SyncStream)j.Args[2]!))
            .ToList();

    /// <summary>
    /// A scope factory that refuses to serve one tenant. The ambient background context is read
    /// rather than a flag passed in, because that is how the orchestrator itself identifies the
    /// tenant it is working for at the moment the scope is created.
    /// </summary>
    private sealed class FailingScopeFactory(IServiceScopeFactory inner, Guid failFor)
        : IServiceScopeFactory
    {
        public IServiceScope CreateScope()
            => BackgroundJobContext.CurrentTenant?.CurrentTenantId == failFor
                ? throw new InvalidOperationException("This tenant's store is unreachable.")
                : inner.CreateScope();
    }
}
