namespace Sankore.Modules.Leads.Features.DispatchLead.Events;

using Sankore.Modules.Leads.Domain;
using Sankore.Shared.Kernel;

/// <summary>
/// Published when a lead is successfully dispatched. Consumed by:
///  - Notifications module (push/SMS to the agent)
///  - Analytics/BI (funnel, agent dashboards — F13.34/F13.35)
///  - Users module (increments AppUser.ActiveLeadsCount via its own handler)
/// </summary>
public sealed record LeadDispatchedEvent(
    Guid LeadId,
    Guid AgentId,
    DispatchingStrategy Strategy,
    double Score,
    DateTimeOffset SlaDeadline) : IntegrationEventBase;

/// <summary>
/// Published when no compatible agent could be found (F13.9 scenario 2).
/// Consumed by Notifications to alert the branch manager and by Analytics
/// to flag coverage gaps (e.g. missing a language in a given agency).
/// </summary>
public sealed record LeadDispatchingFailedEvent(
    Guid LeadId,
    string Reason) : IntegrationEventBase;

/// <summary>
/// Published when the anti-monopoly rule excludes the otherwise-best agent
/// (F13.15). Consumed by Analytics for manager visibility — this is a
/// signal about fairness enforcement, not a failure.
/// </summary>
public sealed record AntiMonopolyTriggeredEvent(
    Guid LeadId,
    int Threshold) : IntegrationEventBase;
