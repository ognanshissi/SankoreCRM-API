namespace Sankore.Modules.Integration.Features.Batch.Inbound;

using Microsoft.Extensions.DependencyInjection;
using Sankore.Modules.Integration.Infrastructure;
using Sankore.Modules.Integration.PublicApi;
using Sankore.Shared.Infrastructure.Messaging;

/// <summary>
/// Publishes INT-25's overdue-acknowledgement alert through the module's outbox.
///
/// <para>
/// The sweep could not write this itself: an integration event belongs in the PublicApi, which
/// that slice does not own, so it was built behind <see cref="IBatchAckOverdueAlerter"/> and left
/// to be closed here. The slice resolves it with <c>GetRequiredService</c> on purpose — a
/// deployment that forgot this registration fails its first sweep visibly, rather than running
/// for months with criterion 3 silently absent.
/// </para>
///
/// <para>
/// It does <b>not</b> save. <c>AckOverdueSweep</c>'s single <c>SaveChangesAsync</c> is what makes
/// the outbox row and the file's status change atomic: an alert without the status change would
/// repeat every quarter hour, and a status change without the alert would leave a stalled batch
/// cycle with nobody told. Publishing here and saving there is the whole reason the seam exists.
/// </para>
/// </summary>
internal sealed class OutboxBatchAckOverdueAlerter(
    [FromKeyedServices(nameof(IntegrationDbContext))] IEventPublisher publisher)
    : IBatchAckOverdueAlerter
{
    /// <summary>
    /// Published as the CONCRETE record type, never through a base-typed variable: the outbox
    /// stores <c>typeof(TEvent).AssemblyQualifiedName</c>, so a base-typed publish would persist
    /// the base's name and no consumer would ever bind to it.
    /// </summary>
    public Task AlertAsync(BatchAckOverdueAlert alert, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(alert);

        return publisher.PublishAsync(
            new IntegrationBatchAckOverdueEvent(
                TenantId: alert.TenantId,
                ConnectionId: alert.ConnectionId,
                BatchFileId: alert.BatchFileId,
                FileName: alert.FileName,
                SequenceNo: alert.SequenceNo,
                SentAt: alert.SentAt,
                AckTimeoutHours: alert.AckTimeoutHours,
                PendingCommandCount: alert.PendingCommandCount,
                DetectedAt: alert.DetectedAt),
            ct);
    }
}
