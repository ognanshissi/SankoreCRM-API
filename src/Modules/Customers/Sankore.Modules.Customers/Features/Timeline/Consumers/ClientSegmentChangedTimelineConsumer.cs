namespace Sankore.Modules.Customers.Features.Timeline.Consumers;

using Sankore.Modules.Customers.Features.Timeline.Projection;
using Sankore.Modules.Customers.Infrastructure;
using Sankore.Modules.Customers.PublicApi.Events;

/// <summary>
/// Projects a segment change. Published by the nightly segmentation command of this same zone
/// (US-M01-BE-27) — the loop is deliberate: the segmentation handler owns
/// <c>ClientSegmentHistory</c> and the client column, while the timeline is fed only through
/// the event, so a manual re-segmentation or a future producer lands the same way.
/// </summary>
public sealed class ClientSegmentChangedTimelineConsumer(
    IInboxGuard inbox,
    IClientTimelineProjector projector) : TimelineConsumerBase<ClientSegmentChangedEvent>(inbox)
{
    protected override Guid TenantIdOf(ClientSegmentChangedEvent evt) => evt.TenantId;

    protected override Task ProjectAsync(ClientSegmentChangedEvent evt, CancellationToken ct)
    {
        var summary = evt.PreviousSegment is null
            ? $"Segment attribué : {evt.NewSegment}"
            : $"Segment modifié : {evt.PreviousSegment} → {evt.NewSegment}";

        return projector.AppendAsync(
            tenantId: evt.TenantId,
            clientId: evt.ClientId,
            sourceModule: TimelineSourceModules.Customers,
            entryType: ClientTimelineEntryTypes.SegmentChanged,
            occurredAt: evt.OccurredAt,
            summary: summary,
            referenceType: "ClientSegment",
            referenceId: evt.NewSegment,
            dedupKey: TimelineDedupKey.ForEvent(
                TimelineSourceModules.Customers, ClientTimelineEntryTypes.SegmentChanged, evt.EventId),
            ct: ct);
    }
}
