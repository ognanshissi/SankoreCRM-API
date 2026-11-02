using Sankore.Shared.Kernel;

namespace Sankore.Modules.Workflow.Domain.Events;

/// <summary>
/// The circuit ended on a « Fin » step that carries an action for the record's module. Turned into a
/// <c>WorkflowEndActionRequested</c> on the outbox by <c>WorkflowDbContext</c>.
/// </summary>
public sealed record EndStepReachedEvent(
    Guid InstanceId,
    Guid TenantId,
    string EntityType,
    Guid EntityId,
    Guid InstanceStepId,
    string Action,
    bool Approved) : DomainEventBase;
