using Sankore.Shared.Kernel;

namespace Sankore.Modules.Workflow.Domain.Events;

/// <summary>
/// Raised when an approver rejects a step, stopping the workflow.
/// </summary>
public sealed record WorkflowRejectedEvent(
    Guid InstanceId,
    Guid TenantId,
    string EntityType,
    Guid EntityId) : DomainEventBase;
