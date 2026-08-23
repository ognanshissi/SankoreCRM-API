using Sankore.Shared.Kernel;

namespace Sankore.Modules.Workflow.Domain.Events;

/// <summary>
/// Integration event published on the MassTransit bus when a workflow instance
/// completes all its steps. Consumers in other modules (e.g. Leads) subscribe to
/// this event to advance their own processes.
/// </summary>
public sealed record WorkflowCompletedIntegrationEvent(
    Guid EventId,
    Guid InstanceId,
    Guid TenantId,
    string EntityType,
    Guid EntityId,
    DateTimeOffset OccurredAt) : IIntegrationEvent;
