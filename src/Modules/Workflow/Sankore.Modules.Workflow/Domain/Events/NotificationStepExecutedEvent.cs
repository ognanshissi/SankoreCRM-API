using Sankore.Shared.Kernel;

namespace Sankore.Modules.Workflow.Domain.Events;

/// <summary>
/// A « Notification » step was franchie automatically. <c>WorkflowDbContext</c> turns it into a
/// <c>WorkflowNotificationRequested</c> in the outbox, in the same transaction.
/// </summary>
public sealed record NotificationStepExecutedEvent(
    Guid InstanceId,
    Guid TenantId,
    string EntityType,
    Guid EntityId,
    Guid InstanceStepId,
    string StepName,
    string? ConfigJson) : DomainEventBase;
