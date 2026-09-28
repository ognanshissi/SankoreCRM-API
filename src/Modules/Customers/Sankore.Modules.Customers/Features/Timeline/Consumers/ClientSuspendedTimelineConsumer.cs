namespace Sankore.Modules.Customers.Features.Timeline.Consumers;

using Sankore.Modules.Customers.Features.Timeline.Projection;
using Sankore.Modules.Customers.Infrastructure;
using Sankore.Modules.Customers.PublicApi.Events;

/// <summary>
/// Projects a suspension. The operator-entered reason is echoed, but it goes through
/// <see cref="TimelineSummaryGuard"/> like every other summary: free text is exactly where a
/// phone number or a document number would leak in.
/// </summary>
public sealed class ClientSuspendedTimelineConsumer(
    IInboxGuard inbox,
    IClientTimelineProjector projector) : TimelineConsumerBase<ClientSuspendedEvent>(inbox)
{
    protected override Guid TenantIdOf(ClientSuspendedEvent evt) => evt.TenantId;

    protected override Task ProjectAsync(ClientSuspendedEvent evt, CancellationToken ct)
        => projector.AppendAsync(
            tenantId: evt.TenantId,
            clientId: evt.ClientId,
            sourceModule: TimelineSourceModules.Customers,
            entryType: ClientTimelineEntryTypes.ClientSuspended,
            occurredAt: evt.OccurredAt,
            summary: $"Client suspendu — motif : {evt.Reason}",
            referenceType: "Client",
            referenceId: evt.ClientId.ToString("D"),
            dedupKey: TimelineDedupKey.ForEvent(
                TimelineSourceModules.Customers, ClientTimelineEntryTypes.ClientSuspended, evt.EventId),
            ct: ct);
}
