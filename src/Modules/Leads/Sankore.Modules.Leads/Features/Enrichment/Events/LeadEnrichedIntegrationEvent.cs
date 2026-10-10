namespace Sankore.Modules.Leads.Features.Enrichment.Events;

using Sankore.Shared.Kernel;

/// <summary>
/// A lead's file was enriched after dispatching (BNF-B-04) — published through the outbox, in the
/// lead's own transaction.
///
/// <para>
/// <b>Identifiers and field NAMES only.</b> An enrichment touches the postal address and the birth
/// date, which is exactly what this module spent a migration encrypting; putting the values on an
/// event would create a second, unencrypted copy in <c>leads.outbox_messages</c> that nothing
/// would ever purge. A consumer that needs a value reads it through the owning module's audited
/// endpoint — the same rule <c>ILeadsModule.GetLeadHistoryAsync</c> already follows.
/// </para>
/// </summary>
/// <param name="ProductLineChanged">
/// Whether this enrichment resolved or cleared the product line. Carried separately because it is
/// the one change with downstream consequences: the required fields, the questionnaires owed and
/// the KYC tier all follow from it.
/// </param>
public sealed record LeadEnrichedIntegrationEvent(
    Guid TenantId,
    Guid LeadId,
    Guid? ProductLineId,
    bool ProductLineChanged,
    IReadOnlyList<string> ChangedFields,
    Guid? SourceActivityId) : IntegrationEventBase;
