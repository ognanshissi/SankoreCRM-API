using Sankore.Shared.Kernel;

namespace Sankore.Modules.Workflow.Domain.Events;

/// <summary>
/// Raised when every step in a <see cref="WorkflowInstance"/> has been approved.
/// Published on the MassTransit bus so other modules (e.g. Leads) can react.
/// </summary>
public sealed record WorkflowCompletedEvent(
    Guid InstanceId,
    Guid TenantId,
    string EntityType,
    Guid EntityId) : DomainEventBase;
