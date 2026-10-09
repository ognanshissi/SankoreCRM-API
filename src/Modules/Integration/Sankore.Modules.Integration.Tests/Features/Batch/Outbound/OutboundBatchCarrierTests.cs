namespace Sankore.Modules.Integration.Tests.Features.Batch.Outbound;

using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Sankore.Modules.Integration.Domain;
using Sankore.Modules.Integration.Features.Batch.Outbound;
using Sankore.Modules.Integration.PublicApi;
using Sankore.Modules.Integration.Tests.TestSupport;
using Xunit;

/// <summary>
/// The one definition of "this connection's writes leave in a file", and the agreement between the
/// two halves that read it.
///
/// <para>
/// <b>What this suite is for.</b> The definition used to be written twice. L7 taught the
/// dispatcher that a <see cref="IntegrationMode.Relay"/> connection carrying batch coordinates
/// takes the file path — the relay agent's file carrier is delivered, its order channel is not —
/// and left the SCHEDULED generation scanning <c>Mode == Batch</c> alone. The combination is worse
/// than either half being wrong on its own: the dispatcher enlisted the command into a file and
/// moved it to <c>Batched</c>, while the scheduled pass that would have DEPOSITED that file never
/// looked at the connection. The deposit and the purge belong to the batch job, not to the
/// dispatcher, so the file stayed in the store, the far end received nothing, and the command
/// burned its whole <c>AckTimeoutHours</c> before anything reported it.
/// </para>
///
/// <para>
/// It was latent when found — every batch-capable kind has a blocked adapter whose health check
/// cannot pass, so no such connection can be activated — and that is exactly why it needs a test
/// rather than a comment: it goes live the day a vendor specification arrives.
/// </para>
/// </summary>
public sealed class OutboundBatchCarrierTests
{
    private static readonly Guid Tenant = new("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    private static readonly Guid Actor = new("44444444-4444-4444-4444-444444444444");
    private static readonly Guid Agent = new("55555555-5555-5555-5555-555555555555");
    private static readonly Guid ConnectionId = new("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");

    private static IntegrationConnection Connection(
        IntegrationMode mode, ConnectionSettings settings, Guid? relayAgentId = null)
        => IntegrationConnection.Create(
            tenantId: Tenant,
            family: settings is OrassSettings ? IntegrationFamily.Insurance : IntegrationFamily.CoreBanking,
            kind: settings.ExpectedKind,
            mode: mode,
            name: "Connexion",
            settings: settings,
            createdBy: Actor,
            clock: new OutboundBatchTestContext.FixedClock(OutboundBatchTestContext.BeforeCutOff),
            relayAgentId: relayAgentId);

    public static TheoryData<IntegrationMode, bool> BatchCapableModes() => new()
    {
        // The mode's own definition.
        { IntegrationMode.Batch, true },

        // THE case the drift lost. Batch coordinates plus a relay: the file is produced here and
        // deposited by the agent, so the scheduled pass owes it a deposit like any other.
        { IntegrationMode.Relay, true },

        // An API connection calls; nothing leaves in a file.
        { IntegrationMode.Api, false },
    };

    [Theory]
    [MemberData(nameof(BatchCapableModes))]
    public void A_batch_capable_connection_leaves_in_a_file_in_batch_and_relay_mode(
        IntegrationMode mode, bool expected)
    {
        var connection = Connection(
            mode,
            new PerfectVisionSettings { SftpHost = "sftp.example.ci", OutboundDirectory = "/upload" },
            relayAgentId: mode == IntegrationMode.Relay ? Agent : null);

        OutboundBatchCarrier.LeavesInAFile(connection).Should().Be(expected);
    }

    [Theory]
    [InlineData(IntegrationMode.Api)]
    [InlineData(IntegrationMode.Relay)]
    public void A_connection_with_no_file_coordinates_never_leaves_in_a_file(IntegrationMode mode)
    {
        // TemenosSettings is not BatchCapableSettings. In Relay mode this is the connection the
        // dispatcher refuses outright rather than calling from this process — and it must not be
        // picked up by the scheduled generation either, which would produce a file for a
        // connection that has nowhere to put one.
        var connection = Connection(
            mode, new TemenosSettings { BaseUrl = "https://cbs.example.ci/api/" });

        OutboundBatchCarrier.LeavesInAFile(connection).Should().BeFalse();
    }

    [Fact]
    public void An_insurance_connection_is_treated_the_same_as_a_core_banking_one()
    {
        // The carrier is a property of the coordinates, not of the family — ASS-01's whole claim
        // is that the batch socle serves both. ORASS in Relay mode with file coordinates is the
        // bordereau-by-relay case of ASS-06 criterion 2.
        var orass = Connection(
            IntegrationMode.Relay,
            new OrassSettings { SftpHost = "sftp.assureur.ci", OutboundDirectory = "/bordereaux" },
            relayAgentId: Agent);

        OutboundBatchCarrier.LeavesInAFile(orass).Should().BeTrue();
    }

    [Theory]
    [InlineData(IntegrationMode.Batch, true)]
    [InlineData(IntegrationMode.Relay, true)]
    [InlineData(IntegrationMode.Api, false)]
    public void The_sql_superset_admits_every_mode_the_real_predicate_can_accept(
        IntegrationMode mode, bool expected)
    {
        // The contract between the two halves: a scan narrows on MayLeaveInAFile in SQL and then
        // applies LeavesInAFile to what came back. If the superset ever stopped admitting a mode
        // the real predicate accepts, the scheduled pass would silently skip those connections
        // again — which is precisely the defect, in its next disguise.
        OutboundBatchCarrier.MayLeaveInAFile(mode).Should().Be(expected);
    }

    [Fact]
    public void The_superset_is_a_superset_of_the_predicate_over_every_mode()
    {
        // Stated over the whole enum rather than case by case, so a fifth IntegrationMode cannot
        // be added on one side only.
        foreach (var mode in Enum.GetValues<IntegrationMode>())
        {
            var batchCapable = Connection(
                mode,
                new PerfectVisionSettings { SftpHost = "sftp.example.ci" },
                relayAgentId: mode == IntegrationMode.Relay ? Agent : null);

            if (OutboundBatchCarrier.LeavesInAFile(batchCapable))
            {
                OutboundBatchCarrier.MayLeaveInAFile(mode).Should().BeTrue(
                    $"the scheduled scan must not filter out {mode}, which the dispatcher batches");
            }
        }
    }

    [Fact]
    public async Task The_scheduled_pass_generates_AND_DEPOSITS_for_a_relay_connection()
    {
        // The behavioural half, and the one that matters: the predicate tests above would pass on
        // a version where the scheduled pass still filtered Relay out in SQL, because they never
        // run the pass. Under the drift this test found a generated file sitting in the store with
        // nothing deposited — the command Batched, the far end empty, and AckTimeoutHours running.
        using var factory = new TestIntegrationDbContextFactory(Tenant);
        var (store, _) = OutboundBatchTestContext.Store();
        var transport = new RecordingFileTransport();

        await using (var seed = factory.CreateContext())
        {
            // relayAgentId makes BatchConnection mode Relay — batch coordinates reached through
            // the on-premise agent, which is INT-24 over INT-26's delivered half.
            seed.Connections.Add(OutboundBatchTestContext.BatchConnection(
                Tenant, OutboundBatchTestContext.BeforeCutOff,
                id: ConnectionId, relayAgentId: Agent));

            seed.Commands.Add(OutboundBatchTestContext.Queued(
                Tenant, ConnectionId, OutboundBatchTestContext.BeforeCutOff));

            await seed.SaveChangesAsync();
        }

        await using (var db = factory.CreateContext())
        {
            var report = await GenerateTenantOutboundBatchFilesJob.RunAsync(
                db,
                OutboundBatchTestContext.Generator(
                    db, OutboundBatchTestContext.AfterCutOff, store, transport),
                NullLogger.Instance,
                Tenant,
                CancellationToken.None);

            report.Connections.Should().Be(
                1, "a relay connection with file coordinates owes a pass like any other");
        }

        // THE assertion. A generated file that is never deposited is the defect's signature.
        transport.Deposits.Should().HaveCount(1);

        await using var check = factory.CreateContext();
        (await check.BatchFiles.IgnoreQueryFilters().SingleAsync()).Status
            .Should().Be(BatchFileStatus.Sent);
    }

    [Fact]
    public async Task The_orchestrator_sees_a_tenant_whose_only_batch_connection_is_a_relay_one()
    {
        // The fan-out gate, one level above. It answered "this tenant owes nothing" and so the
        // pass above was never even enqueued — which is why the drift produced no error anywhere.
        using var factory = new TestIntegrationDbContextFactory(Tenant);

        await using (var seed = factory.CreateContext())
        {
            seed.Connections.Add(OutboundBatchTestContext.BatchConnection(
                Tenant, OutboundBatchTestContext.BeforeCutOff,
                id: ConnectionId, relayAgentId: Agent));

            await seed.SaveChangesAsync();
        }

        await using var db = factory.CreateContext();

        (await OutboundBatchOrchestratorJob.HasBatchConnectionAsync(db, Tenant, CancellationToken.None))
            .Should().BeTrue();
    }
}
