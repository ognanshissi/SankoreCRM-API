namespace Sankore.Modules.Customers.Features.Timeline.Consumers;

using Sankore.Modules.Customers.Features.Timeline.Projection;
using Sankore.Modules.Customers.Infrastructure;
using Sankore.Modules.Customers.PublicApi.Events;

/// <summary>
/// Projects the archival. The entry survives the archival on purpose: the record becomes
/// read-only, the history does not disappear — it is what the retention job later reasons on.
/// </summary>
public sealed class ClientArchivedTimelineConsumer(
    IInboxGuard inbox,
    IClientTimelineProjector projector) : TimelineConsumerBase<ClientArchivedEvent>(inbox)
{
    protected override Guid TenantIdOf(ClientArchivedEvent evt) => evt.TenantId;

    protected override Task ProjectAsync(ClientArchivedEvent evt, CancellationToken ct)
        => projector.AppendAsync(
            tenantId: evt.TenantId,
            clientId: evt.ClientId,
            sourceModule: TimelineSourceModules.Customers,
            entryType: ClientTimelineEntryTypes.ClientArchived,
            occurredAt: evt.ArchivedAt,
            summary: $"Dossier archivé — motif : {evt.Reason}",
            referenceType: "Client",
            referenceId: evt.ClientId.ToString("D"),
            dedupKey: TimelineDedupKey.ForEvent(
                TimelineSourceModules.Customers, ClientTimelineEntryTypes.ClientArchived, evt.EventId),
            ct: ct);
}
