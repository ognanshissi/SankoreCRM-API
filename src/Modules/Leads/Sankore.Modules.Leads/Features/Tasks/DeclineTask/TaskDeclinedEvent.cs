namespace Sankore.Modules.Leads.Features.Tasks.DeclineTask;

using Sankore.Shared.Kernel;

/// <summary>
/// Published when an agent declines a CRM task (US-M13-084).
/// Consumed by Notifications (alert manager) and Analytics (decline-rate tracking).
/// </summary>
public sealed record TaskDeclinedEvent(
    Guid TaskId,
    Guid TenantId,
    Guid AgentId,
    string Reason,
    DateTimeOffset DeclinedAt) : IntegrationEventBase;
