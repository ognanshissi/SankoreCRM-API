namespace Sankore.Modules.Leads.Features.Household.Events;

using Sankore.Shared.Kernel;

/// <summary>
/// Published when a lead's PEP exposure flips (BNF-B-05). Consumed by dispatching, which routes an
/// exposed lead to a cleared agent, and by M08 for the compliance notification.
///
/// <para>
/// <b>The FLAG only, never the detail.</b> The function and the country together often name the
/// person, and reading them is gated on <c>lead:pep:read</c> — an event carrying them would put
/// that detail into every consumer's logs and into the outbox table, past the permission.
/// </para>
/// </summary>
public sealed record LeadPepExposureChangedIntegrationEvent(
    Guid TenantId,
    Guid LeadId,
    bool HasPepExposure) : IntegrationEventBase;
