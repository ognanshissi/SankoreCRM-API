namespace Sankore.Modules.Leads.Features.Tasks.Events;

using Sankore.Shared.Kernel;

/// <summary>
/// Published when a CRM task is assigned to an agent (manual, dispatch, or reassignment).
/// Consumed by <see cref="Consumers.TaskAssignedNotificationConsumer"/> to send an email alert via M08.
/// </summary>
public sealed record TaskAssignedIntegrationEvent(
    Guid TaskId,
    Guid TenantId,
    Guid AgentId,
    string Title,
    DateTimeOffset DueAt,
    Guid? LeadId) : IntegrationEventBase;
