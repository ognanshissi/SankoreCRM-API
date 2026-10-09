namespace Sankore.Modules.Integration.Features.Batch.Outbound;

using Hangfire;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Sankore.Modules.Integration.Domain;
using Sankore.Modules.Integration.Infrastructure;
using Sankore.Modules.Integration.PublicApi;
using Sankore.Shared.Infrastructure.BackgroundJobs;

/// <summary>
/// Hangfire job — the outbound batch pass of ONE tenant (INT-24, criterion 2). Enqueued by
/// <see cref="OutboundBatchOrchestratorJob"/>; its only argument is an opaque tenant identifier.
///
/// <para>
/// <b>On the named queue <c>integration-batch</c></b> (criterion 6). An SFTP transfer can hold a
/// worker for minutes, which is exactly why it must not share a pool with the interactive work on
/// <c>default</c> — a lead import an agent is watching behind a stalled file transfer is the
/// failure this queue exists to prevent. <c>DispatchServiceRegistration</c> carries the
/// <c>BackgroundJobServerOptions</c> the host must declare, and this queue is already in its
/// list; <b>without that wiring the job is enqueued to a queue no worker reads and never runs at
/// all</b>, with nothing in the logs.
/// </para>
///
/// <para>
/// <b>Three phases, in this order, per connection: generate, deposit, purge.</b> The order is the
/// argument. Generation first so the file this cut-off owes exists before anything is sent;
/// deposit second so the same pass sends it AND retries any earlier file whose transfer failed;
/// purge last and only over acknowledged files, so a purge can never remove something the deposit
/// phase of the same run might still have needed.
/// </para>
///
/// <para>
/// One connection's failure does not stop the next. A tenant may hold a core banking connection
/// and two insurers, and an SFTP server being down at one of them is not a reason for the other
/// two to skip their cut-off.
/// </para>
/// </summary>
[Queue(QueueName)]
public sealed class GenerateTenantOutboundBatchFilesJob(IServiceScopeFactory scopeFactory)
{
    /// <summary>
    /// The batch queue. Declared here so the name has one source, and already listed by
    /// <c>DispatchServiceRegistration.Queues</c> — which the host reads when it configures its
    /// Hangfire server.
    /// </summary>
    public const string QueueName = "integration-batch";

    public async Task ExecuteAsync(Guid tenantId)
    {
        // Set BEFORE the scope is created. ITenantContext and ICurrentUser are built from this
        // ambient context when the scope resolves them, and a scope opened first would capture the
        // HTTP implementations and find no request — the precedent, with the same comment, is
        // DispatchTenantCommandsJob. The actor is SYSTEM: nobody decided that a cut-off had
        // arrived, a clock did.
        using var bgCtx = BackgroundJobContext.SetScope(tenantId, Guid.Empty, "SYSTEM");
        using var scope = scopeFactory.CreateScope();

        var sp = scope.ServiceProvider;
        var logger = sp.GetRequiredService<ILogger<GenerateTenantOutboundBatchFilesJob>>();

        var report = await RunAsync(
            sp.GetRequiredService<IntegrationDbContext>(),
            sp.GetRequiredService<OutboundBatchFileGenerator>(),
            logger,
            tenantId,
            CancellationToken.None);

        logger.LogInformation(
            "Outbound batch pass for tenant {TenantId}: {Connections} batch connection(s), "
            + "{Generated} file(s) generated, {Deposited} deposited, {DepositFailures} deposit "
            + "failure(s), {Purged} purged, {Errors} connection(s) in error.",
            tenantId, report.Connections, report.Generated, report.Deposited,
            report.DepositFailures, report.Purged, report.Errors);
    }

    /// <summary>
    /// The pass itself, with its collaborators passed in rather than resolved — so a test can pin
    /// it against an InMemory context without a DI container or a Hangfire storage. The scope
    /// handling is the only thing <see cref="ExecuteAsync"/> adds, and it is identical in every
    /// job of this repository. Same split as <c>DispatchTenantCommandsJob.RunAsync</c>.
    /// </summary>
    internal static async Task<OutboundBatchPassReport> RunAsync(
        IntegrationDbContext db,
        OutboundBatchFileGenerator generator,
        ILogger logger,
        Guid tenantId,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(generator);

        var report = new OutboundBatchPassReport();

        // IgnoreQueryFilters paired with an explicit tenant predicate — the repo-wide rule for a
        // job, where the ambient tenant is not necessarily the one being swept. Inactive
        // connections are excluded: a deactivated connection owes no deposit, and generating for
        // one would write a file nobody will fetch.
        // The mode filter is OutboundBatchCarrier's SQL-translatable SUPERSET; the real question
        // type-tests the settings, which are jsonb behind a value converter, so it is asked of the
        // loaded rows. One definition, two halves — see OutboundBatchCarrier for what the drift
        // between them cost.
        var candidates = await db.Connections
            .IgnoreQueryFilters()
            .Where(c => c.TenantId == tenantId
                     && c.IsActive
                     && (c.Mode == IntegrationMode.Batch || c.Mode == IntegrationMode.Relay))
            .OrderBy(c => c.Id)
            .ToListAsync(ct);

        var connections = candidates.Where(OutboundBatchCarrier.LeavesInAFile).ToList();

        report.Connections = connections.Count;

        foreach (var connection in connections)
        {
            try
            {
                var generated = await generator.GenerateAsync(
                    tenantId, connection, seedCommandId: null, ct);

                if (generated.IsFailure)
                {
                    report.Errors++;

                    logger.LogWarning(
                        "Generating the outbound batch file of connection {ConnectionId} failed: "
                        + "{Code} {Detail}", connection.Id, generated.Code, generated.Detail);
                }
                else if (generated.Value.Generated)
                {
                    report.Generated++;
                }

                // Attempted even when generation failed: an earlier file may still be waiting to
                // be deposited, and a misconfigured encoding today must not strand yesterday's
                // file for ever.
                var deposit = await generator.DepositAsync(tenantId, connection, ct);
                report.Deposited += deposit.Deposited;
                report.DepositFailures += deposit.Failed;

                report.Purged += await generator.PurgeAsync(tenantId, connection, ct);
            }
            catch (Exception ex)
            {
                // One connection's fault must not abandon the others' cut-off. The file it was
                // producing is either committed or not — the generator's own transaction decides —
                // and the next pass re-reads committed state.
                report.Errors++;

                logger.LogError(
                    ex, "The outbound batch pass of connection {ConnectionId} (tenant {TenantId}) "
                    + "threw.", connection.Id, tenantId);

                db.ChangeTracker.Clear();
            }
        }

        return report;
    }
}

/// <summary>What one tenant's pass did, for the log line and for the tests.</summary>
internal sealed class OutboundBatchPassReport
{
    public int Connections { get; set; }

    /// <summary>Files produced. A quiet connection produces none, which is not an error.</summary>
    public int Generated { get; set; }

    public int Deposited { get; set; }

    /// <summary>Files whose transfer failed. They stay <c>Generated</c> and are retried.</summary>
    public int DepositFailures { get; set; }

    /// <summary>Acknowledged files whose content was deleted, the row kept.</summary>
    public int Purged { get; set; }

    public int Errors { get; set; }
}
