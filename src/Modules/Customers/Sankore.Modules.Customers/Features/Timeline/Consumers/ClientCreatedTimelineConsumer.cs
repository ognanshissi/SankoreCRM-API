namespace Sankore.Modules.Customers.Features.Timeline.Consumers;

using Microsoft.Extensions.Logging;
using Sankore.Modules.Customers.Features.Timeline.Projection;
using Sankore.Modules.Customers.Infrastructure;
using Sankore.Modules.Customers.PublicApi.Events;
using Sankore.Modules.Leads.PublicApi;

/// <summary>
/// Opens the client timeline, and — when the client came from a converted lead — imports the
/// lead's commercial history so the relationship does not appear to start at conversion
/// (US-M01-BE-26 / F13.29).
///
/// The import is best-effort by design: a lead history that cannot be read (Leads module not
/// wired in this deployment, lead purged, transient failure) must not prevent the
/// CLIENT_CREATED entry from existing. It is logged and skipped; a replay re-attempts it, and
/// the dedup key makes the retry safe.
/// </summary>
public sealed class ClientCreatedTimelineConsumer(
    IInboxGuard inbox,
    IClientTimelineProjector projector,
    ILogger<ClientCreatedTimelineConsumer> logger,
    // Optional on purpose: ILeadsModule is only registered when the Leads module is part of
    // the host. A deployment without it still gets a working client timeline.
    ILeadsModule? leads = null) : TimelineConsumerBase<ClientCreatedEvent>(inbox)
{
    protected override Guid TenantIdOf(ClientCreatedEvent evt) => evt.TenantId;

    protected override async Task ProjectAsync(ClientCreatedEvent evt, CancellationToken ct)
    {
        // Note what is NOT in the summary: no client number (not sensitive, but useless here —
        // the entry is already scoped to the client), no name, no contact value.
        // Static string.Equals rather than the instance method: ClientType is non-nullable by
        // contract, but a consumer is the wrong place to turn a broken contract into a retry storm.
        var summary = string.Equals(evt.ClientType, "Legal", StringComparison.OrdinalIgnoreCase)
            ? "Personne morale enregistrée (dossier en attente de KYC)"
            : "Client enregistré (dossier en attente de KYC)";

        if (evt.SourceLeadId is not null)
            summary += " — issu d'un prospect converti";

        await projector.AppendAsync(
            tenantId: evt.TenantId,
            clientId: evt.ClientId,
            sourceModule: TimelineSourceModules.Customers,
            entryType: ClientTimelineEntryTypes.ClientCreated,
            occurredAt: evt.OccurredAt,
            summary: summary,
            referenceType: "Client",
            referenceId: evt.ClientId.ToString("D"),
            dedupKey: TimelineDedupKey.ForEvent(
                TimelineSourceModules.Customers, ClientTimelineEntryTypes.ClientCreated, evt.EventId),
            ct: ct);

        if (evt.SourceLeadId is not { } leadId) return;

        if (leads is null)
        {
            logger.LogInformation(
                "Client {ClientId} came from lead {LeadId} but no ILeadsModule is registered — "
                + "commercial history not imported.",
                evt.ClientId, leadId);
            return;
        }

        try
        {
            var history = await leads.GetLeadHistoryAsync(evt.TenantId, leadId, ct);

            foreach (var item in history)
            {
                await projector.AppendAsync(
                    tenantId: evt.TenantId,
                    clientId: evt.ClientId,
                    sourceModule: TimelineSourceModules.Leads,
                    entryType: item.EntryType,
                    occurredAt: item.OccurredAt,
                    summary: item.Summary,
                    referenceType: item.ReferenceType,
                    referenceId: item.ReferenceId,
                    // These facts are rows, not events: no EventId to key on.
                    dedupKey: TimelineDedupKey.ForFact(
                        TimelineSourceModules.Leads, item.EntryType, item.ReferenceId,
                        evt.ClientId, item.OccurredAt),
                    ct: ct);
            }

            logger.LogInformation(
                "Imported {Count} lead-history entr(ies) from lead {LeadId} into the timeline of client {ClientId}.",
                history.Count, leadId, evt.ClientId);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(
                ex,
                "Could not import the commercial history of lead {LeadId} for client {ClientId} — "
                + "the CLIENT_CREATED entry is kept; a replay will retry the import.",
                leadId, evt.ClientId);
        }
    }
}
