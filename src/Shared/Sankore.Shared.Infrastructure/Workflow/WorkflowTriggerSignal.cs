namespace Sankore.Shared.Infrastructure.Workflow;

/// <summary>
/// Bus message published by any module to start an event-driven workflow.
/// The Workflow module listens for this signal and auto-starts instances
/// for every active trigger whose EntityType + EventName match.
/// </summary>
public sealed record WorkflowTriggerSignal(
    Guid TenantId,
    string EntityType,
    Guid EntityId,
    string EventName,
    Dictionary<string, object>? Context = null);
