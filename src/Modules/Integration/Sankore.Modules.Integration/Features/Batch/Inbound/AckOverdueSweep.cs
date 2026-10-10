namespace Sankore.Modules.Integration.Features.Batch.Inbound;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Sankore.Modules.Integration.Domain;
using Sankore.Modules.Integration.Infrastructure;

/// <summary>
/// INT-25's criterion 3 — <b>a <c>Batched</c> command with no acknowledgement beyond a
/// configurable delay raises an alert</b>.
///
/// <para>
/// <b>The delay is the connection's</b> <c>BatchCapableSettings.AckTimeoutHours</c> (48 h by
/// default), and <see cref="IntegrationBatchFile.IsAckOverdue"/> is the authority on what
/// "overdue" means — the predicate below mirrors it in SQL because an instance method does not
/// translate, and the two must keep agreeing. It is a per-connection setting rather than a
/// platform constant because the delay belongs to the far end: an IMF whose CBS acknowledges
/// within the hour and one that runs a weekly batch cannot share a threshold.
/// </para>
///
/// <para>
/// <b>The alert is raised PER FILE, not per command.</b> The criterion speaks of a command, and
/// every command of an unacknowledged file is overdue for the same single reason — the file was
/// never answered. One alert per command would send an administrator four hundred notifications
/// about one event, and an alert nobody reads is not an alert. The record therefore carries the
/// count of commands still waiting, which is the figure that says how bad it is.
/// </para>
///
/// <para>
/// <b>Exactly once, and the ledger is the file's own status.</b> The file moves from <c>Sent</c>
/// to <c>Failed</c> in the same save as the outbox row, so
/// <see cref="IntegrationBatchFile.IsAckOverdue"/> — which requires <c>Sent</c> — is false on
/// every later run. There is no second table and no new column: this slice owns neither. The
/// status is also the honest one. A file deposited two days ago and never answered is not a file
/// we may still treat as delivered, and <c>FailureDetail</c> says exactly why in the operator's
/// own list of batch files, next to a bad checksum and an out-of-order sequence.
/// </para>
///
/// <para>
/// <b>The commands are left <c>Batched</c>.</b> Rejecting them would be this platform deciding an
/// outcome the external system never stated: a late CBS that acknowledges on the third day still
/// closes them correctly, because <see cref="AcknowledgementApplier"/> matches on the command and
/// never reads the outbound file's status. A command we closed wrongly cannot be un-closed.
/// </para>
/// </summary>
internal static class AckOverdueSweep
{
    /// <summary>
    /// Fallback delay for a connection whose settings are not file-based. A batch file on such a
    /// connection should not exist at all; alerting on the domain's own default after two days is
    /// a better answer than never alerting, because the file is evidence that something deposited
    /// one.
    /// </summary>
    private const int DefaultAckTimeoutHours = 48;

    /// <summary>
    /// Sweeps one connection's outbound files and alerts on each one that is overdue. Returns how
    /// many alerts were raised.
    /// </summary>
    public static async Task<int> RunAsync(
        IntegrationDbContext db,
        IBatchAckOverdueAlerter alerter,
        TimeProvider clock,
        ILogger logger,
        Guid tenantId,
        IntegrationConnection connection,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(alerter);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(connection);

        var now = clock.GetUtcNow();

        var timeoutHours = connection.Settings is BatchCapableSettings batch
                           && batch.AckTimeoutHours > 0
            ? batch.AckTimeoutHours
            : DefaultAckTimeoutHours;

        // IgnoreQueryFilters paired with an explicit tenant predicate, as every background path in
        // this repository does. The predicate MIRRORS IntegrationBatchFile.IsAckOverdue; the
        // domain method below stays the authority, so a change to the rule cannot be half-applied.
        var candidates = await db.BatchFiles
            .AsTracking()
            .IgnoreQueryFilters()
            .Where(f => f.TenantId == tenantId
                     && f.ConnectionId == connection.Id
                     && f.Direction == BatchDirection.Out
                     && f.Status == BatchFileStatus.Sent
                     && f.SentAt != null)
            .ToListAsync(ct);

        var raised = 0;

        foreach (var file in candidates.Where(f => f.IsAckOverdue(now, timeoutHours)))
        {
            // Counted rather than loaded: the alert needs the number of commands still waiting,
            // and an integration command's payload — a customer's identity document on its way
            // out of the platform — has no business in a sweep's memory.
            var pending = await db.Commands
                .IgnoreQueryFilters()
                .CountAsync(
                    c => c.TenantId == tenantId
                      && c.BatchFileId == file.Id
                      && c.Status == CommandStatus.Batched, ct);

            // A file whose commands all closed by other means is NOT what criterion 3 is about:
            // the criterion is a command left waiting, and there is none. Its status is left
            // exactly as it is — deciding what becomes of an unanswered file whose work is
            // nevertheless done belongs to INT-24's own acknowledgement and retention handling,
            // not to an alert sweep. The cost is that such a file stays Sent and is re-examined
            // (one count query) on every later run.
            if (pending == 0) continue;

            await alerter.AlertAsync(
                new BatchAckOverdueAlert(
                    TenantId: tenantId,
                    ConnectionId: connection.Id,
                    BatchFileId: file.Id,
                    FileName: file.FileName,
                    SequenceNo: file.SequenceNo,
                    SentAt: file.SentAt!.Value,
                    AckTimeoutHours: timeoutHours,
                    PendingCommandCount: pending,
                    DetectedAt: now),
                ct);

            raised++;

            file.MarkFailed(
                $"{InboundBatchCodes.AckOverdue}: no acknowledgement within {timeoutHours} h; "
                + $"{pending} command(s) still Batched.",
                clock);

            // ONE save: the outbox row the alerter added to this same context and the status that
            // stops the alert being raised again. Published first and recorded after, a crash in
            // between would alert again on the next run; recorded first and published after, the
            // alert would be lost for good. Same argument, same shape, as KycLimitWatchJob.
            await db.SaveChangesAsync(ct);

            logger.LogWarning(
                "Batch file {FileName} (sequence {SequenceNo}) of connection {ConnectionId} was "
                + "not acknowledged within {TimeoutHours} h; {Pending} command(s) still waiting.",
                file.FileName, file.SequenceNo, connection.Id, timeoutHours, pending);
        }

        return raised;
    }
}

/// <summary>
/// The fact criterion 3 publishes: one outbound file we deposited was never acknowledged, and
/// commands are still waiting on it.
///
/// <para>
/// A plain record and not an integration event, because <b>this slice does not own
/// <c>PublicApi/IntegrationEvents.cs</c></b> and the event it needs does not exist yet. See
/// <see cref="IBatchAckOverdueAlerter"/> and the registration's remarks for the two-line
/// completion.
/// </para>
/// </summary>
internal sealed record BatchAckOverdueAlert(
    Guid TenantId,
    Guid ConnectionId,
    Guid BatchFileId,
    string FileName,
    long SequenceNo,
    DateTimeOffset SentAt,
    int AckTimeoutHours,
    int PendingCommandCount,
    DateTimeOffset DetectedAt);

/// <summary>
/// Publishes an overdue-acknowledgement alert through the module's outbox.
///
/// <para>
/// ───────────────────────────────────────────────────────────────────────────────────────────<br/>
/// <b>NO IMPLEMENTATION IS REGISTERED BY THIS SLICE.</b> The alert of criterion 3 must be an
/// integration event — nobody reads the batch table unprompted, and M08 is what turns the fact
/// into a notification — and an integration event belongs in
/// <c>Sankore.Modules.Integration.PublicApi</c> so a consumer never references this assembly.
/// That file is owned elsewhere, and the record this alert needs is not in it yet.
/// </para>
///
/// <para>
/// <b>The seam fails closed.</b> <see cref="PollInboundBatchFilesJob"/> resolves this interface
/// with <c>GetRequiredService</c>, so a deployment without the implementation fails its first
/// inbound sweep loudly, in the Hangfire dashboard. A no-op default would have been a criterion
/// that silently does nothing while every test still passes — which is how an alert nobody
/// receives gets shipped.
/// </para>
///
/// <para>
/// <b>Do not save.</b> The outbox publisher adds its row to the same
/// <c>IntegrationDbContext</c> and leaves it pending on purpose: <see cref="AckOverdueSweep"/>'s
/// single save is what makes the alert and the file's status atomic.
/// </para>
/// </summary>
internal interface IBatchAckOverdueAlerter
{
    Task AlertAsync(BatchAckOverdueAlert alert, CancellationToken ct);
}
