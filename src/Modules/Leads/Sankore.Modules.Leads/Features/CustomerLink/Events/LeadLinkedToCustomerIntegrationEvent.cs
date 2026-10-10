namespace Sankore.Modules.Leads.Features.CustomerLink.Events;

using Sankore.Shared.Kernel;

/// <summary>
/// Published when a lead is attached to an existing M01 client (BNF-B-22). Consumed by M01, which
/// surfaces the customer's requests in progress on the client record.
///
/// <para>
/// <b>Identifiers only.</b> The whole point of BNF-B-23 is that a lead belonging to a known
/// customer keeps NO copy of that customer's identity, household, PEP or FATCA in the leads
/// schema — the client record is the single source. An event carrying any of it would be the first
/// copy, and the one hardest to notice going stale.
/// </para>
/// </summary>
public sealed record LeadLinkedToCustomerIntegrationEvent(
    Guid TenantId,
    Guid LeadId,
    Guid CustomerId,
    bool IsAutomatic) : IntegrationEventBase;

/// <summary>Published when an agent detaches a lead from a client it was wrongly attached to.</summary>
public sealed record LeadDetachedFromCustomerIntegrationEvent(
    Guid TenantId,
    Guid LeadId,
    Guid PreviousCustomerId) : IntegrationEventBase;
