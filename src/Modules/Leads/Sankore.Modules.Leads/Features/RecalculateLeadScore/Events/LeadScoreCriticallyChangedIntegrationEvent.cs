namespace Sankore.Modules.Leads.Features.RecalculateLeadScore.Events;

using Sankore.Shared.Kernel;

/// <summary>
/// Published when a lead score changes by more than the configured delta
/// or when it crosses a key qualification threshold (e.g. 40 or 60).
/// Consumers: Notifications module (alert agents/managers), Analytics.
/// </summary>
public sealed record LeadScoreCriticallyChangedIntegrationEvent(
    Guid LeadId,
    Guid TenantId,
    int PreviousScore,
    int NewScore,
    /// <summary>Signed delta: positive = score increased, negative = score dropped.</summary>
    int Delta,
    string TriggerEvent) : IntegrationEventBase;
