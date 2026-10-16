namespace Sankore.Modules.Integration.Tests.Features.Telephony;

using FluentAssertions;
using Hangfire;
using Hangfire.Common;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Sankore.Modules.Integration.Domain;
using Sankore.Modules.Integration.Features.Sync;
using Sankore.Modules.Integration.Features.Telephony.Journal;
using Sankore.Modules.Integration.Infrastructure;
using Sankore.Modules.Integration.PublicApi;
using Sankore.Modules.Integration.Tests.Features.Sync;
using Sankore.Shared.Kernel;
using Xunit;

/// <summary>
/// Which JOB a sync stream reaches (TEL-B-14).
///
/// <para>
/// This is the suite that would have caught the gap Lot 1 left: <c>SyncStream.Calls</c> was
/// declared and due every fifteen minutes, and the orchestrator dispatched it to
/// <c>IntegrationSyncJob</c> — which reads <c>integration_reference</c> for CUSTOMERS, finds none
/// on a telephony connection, and advances the cursor having done nothing. Fifteen minutes of
/// nothing, every fifteen minutes, with nothing anywhere to say so.
/// </para>
/// </summary>
public sealed class SyncStreamRoutingTests
{
    private static readonly Guid Tenant = Guid.Parse("eeeeeeee-0000-0000-0000-000000000001");

    [Fact]
    public async Task A_telephony_connection_sends_its_Calls_stream_to_the_telephony_sweep()
    {
        var databaseName = $"routing-{Guid.NewGuid()}";
        var provider = SyncTestContext.NewProvider(databaseName);

        Guid connectionId;

        using (var seedScope = provider.CreateScope())
        {
            using var bg = Sankore.Shared.Infrastructure.BackgroundJobs.BackgroundJobContext
                .SetScope(Tenant, Guid.Empty, "SYSTEM");

            var db = seedScope.ServiceProvider.GetRequiredService<IntegrationDbContext>();

            // A TELEPHONY connection, whose only declared stream is Calls — so whatever this
            // orchestrator enqueues is the routing decision under test and nothing else.
            // The FAMILY is the whole of what routing depends on — SyncStreams.For reads it —
            // so the harness's default kind and settings are left alone, as its own comment
            // invites: which kind it is has no bearing here.
            connectionId = SyncTestContext.SeedConnection(
                db, Tenant,
                family: IntegrationFamily.Telephony,
                name: "Téléphonie").Id;

            await db.SaveChangesAsync(CancellationToken.None);
        }

        var hangfire = Substitute.For<IBackgroundJobClient>();

        var tenantStore = Substitute.For<ITenantStore>();
        tenantStore.GetAllActiveAsync(Arg.Any<CancellationToken>())
            .Returns([new TenantInfo(
                Tenant, "Test tenant", "t.sankore.test", true, false, null, null)]);

        await new IntegrationSyncOrchestrator(
                provider.GetRequiredService<IServiceScopeFactory>(),
                tenantStore,
                hangfire,
                NullLogger<IntegrationSyncOrchestrator>.Instance)
            .ExecuteAsync();

        var job = Jobs(hangfire).Should().ContainSingle().Subject;

        job.Type.Should().Be<SyncTelephonyCallsJob>();

        // Two arguments: a tenant and a connection. No stream, because this job sweeps one.
        job.Args.Should().HaveCount(2);
        job.Args[0].Should().Be(Tenant);
        job.Args[1].Should().Be(connectionId);
    }

    [Fact]
    public void Every_SyncStream_value_is_routed()
    {
        // The default arm THROWS rather than falling back to the customer sweep, so a new stream
        // is a loud error on the first minute after the deploy instead of a sweep that silently
        // does nothing for ever. This case is what makes that claim checkable without waiting for
        // somebody to add a stream and notice.
        var routed = new[]
        {
            SyncStream.Customers, SyncStream.Accounts, SyncStream.Transactions,
            SyncStream.Loans, SyncStream.Policies, SyncStream.Claims, SyncStream.Calls,
        };

        Enum.GetValues<SyncStream>().Should().BeEquivalentTo(
            routed,
            "a new SyncStream must be routed in IntegrationSyncOrchestrator, and this list "
            + "updated alongside it");
    }

    private static IReadOnlyList<Job> Jobs(IBackgroundJobClient hangfire)
        => hangfire.ReceivedCalls()
            .Select(c => c.GetArguments()[0])
            .OfType<Job>()
            .ToList();
}
