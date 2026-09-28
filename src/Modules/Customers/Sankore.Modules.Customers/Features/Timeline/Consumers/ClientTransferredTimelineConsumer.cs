namespace Sankore.Modules.Customers.Features.Timeline.Consumers;

using Sankore.Modules.Customers.Features.Timeline.Projection;
using Sankore.Modules.Customers.Infrastructure;
using Sankore.Modules.Customers.PublicApi.Events;

/// <summary>
/// Projects a portfolio transfer. Agency ids are kept raw rather than resolved to names: the
/// timeline must not call another module to render a row, and an agency can be renamed
/// afterwards — the front-end resolves the label at display time.
/// </summary>
public sealed class ClientTransferredTimelineConsumer(
    IInboxGuard inbox,
    IClientTimelineProjector projector) : TimelineConsumerBase<ClientTransferredEvent>(inbox)
{
    protected override Guid TenantIdOf(ClientTransferredEvent evt) => evt.TenantId;

    protected override Task ProjectAsync(ClientTransferredEvent evt, CancellationToken ct)
    {
        var summary = $"Portefeuille transféré de l'agence {evt.FromAgencyId:D} vers {evt.ToAgencyId:D}";
        summary += evt.AdvisorUserId is null
            ? " — conseiller réinitialisé"
            : $" — conseiller {evt.AdvisorUserId:D}";

        return projector.AppendAsync(
            tenantId: evt.TenantId,
            clientId: evt.ClientId,
            sourceModule: TimelineSourceModules.Customers,
            entryType: ClientTimelineEntryTypes.ClientTransferred,
            occurredAt: evt.OccurredAt,
            summary: summary,
            referenceType: "Agency",
            referenceId: evt.ToAgencyId.ToString("D"),
            dedupKey: TimelineDedupKey.ForEvent(
                TimelineSourceModules.Customers, ClientTimelineEntryTypes.ClientTransferred, evt.EventId),
            ct: ct);
    }
}
