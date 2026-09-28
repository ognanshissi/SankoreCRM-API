namespace Sankore.Modules.Customers.Features.Timeline.Consumers;

using Sankore.Modules.Customers.Features.Timeline.Projection;
using Sankore.Modules.Customers.Infrastructure;
using Sankore.Modules.Customers.PublicApi.Events;

/// <summary>
/// Projects a merge onto the SURVIVING client only.
///
/// The absorbed record's own entries are not duplicated here: the merge executor
/// (zone Duplicates) re-parents them with <c>ClientTimelineEntry.ReassignTo</c>, so they are
/// already part of the survivor's history. What this consumer adds is the merge itself —
/// the fact that explains why the history suddenly contains two pasts.
/// </summary>
public sealed class ClientsMergedTimelineConsumer(
    IInboxGuard inbox,
    IClientTimelineProjector projector) : TimelineConsumerBase<ClientsMergedEvent>(inbox)
{
    protected override Guid TenantIdOf(ClientsMergedEvent evt) => evt.TenantId;

    protected override Task ProjectAsync(ClientsMergedEvent evt, CancellationToken ct)
        => projector.AppendAsync(
            tenantId: evt.TenantId,
            clientId: evt.SurvivorClientId,
            sourceModule: TimelineSourceModules.Customers,
            entryType: ClientTimelineEntryTypes.ClientsMerged,
            occurredAt: evt.OccurredAt,
            summary: $"Fusion de dossiers — le dossier {evt.AbsorbedClientId:D} a été absorbé dans celui-ci",
            referenceType: "Client",
            referenceId: evt.AbsorbedClientId.ToString("D"),
            dedupKey: TimelineDedupKey.ForEvent(
                TimelineSourceModules.Customers, ClientTimelineEntryTypes.ClientsMerged, evt.EventId),
            ct: ct);
}
