namespace Sankore.Modules.Customers.Features.Timeline.Consumers;

using Sankore.Modules.Customers.Features.Timeline.Projection;
using Sankore.Modules.Customers.Infrastructure;
using Sankore.Modules.Customers.PublicApi.Events;

/// <summary>Projects "the client became Active" onto the timeline.</summary>
public sealed class ClientActivatedTimelineConsumer(
    IInboxGuard inbox,
    IClientTimelineProjector projector) : TimelineConsumerBase<ClientActivatedEvent>(inbox)
{
    protected override Guid TenantIdOf(ClientActivatedEvent evt) => evt.TenantId;

    protected override Task ProjectAsync(ClientActivatedEvent evt, CancellationToken ct)
        => projector.AppendAsync(
            tenantId: evt.TenantId,
            clientId: evt.ClientId,
            sourceModule: TimelineSourceModules.Customers,
            entryType: ClientTimelineEntryTypes.ClientActivated,
            occurredAt: evt.OccurredAt,
            summary: "Client actif — KYC validé",
            referenceType: "Client",
            referenceId: evt.ClientId.ToString("D"),
            dedupKey: TimelineDedupKey.ForEvent(
                TimelineSourceModules.Customers, ClientTimelineEntryTypes.ClientActivated, evt.EventId),
            ct: ct);
}
