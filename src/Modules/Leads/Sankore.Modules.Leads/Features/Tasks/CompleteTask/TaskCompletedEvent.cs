namespace Sankore.Modules.Leads.Features.Tasks.CompleteTask;

using Sankore.Shared.Kernel;

/// <summary>
/// Published when a CRM task is completed. Consumed by
/// <see cref="Consumers.TaskCompletedTaskConsumer"/> to evaluate
/// TaskGenerationRules and create follow-up tasks (US-M13-093).
/// </summary>
public sealed record TaskCompletedEvent(
    Guid TaskId,
    Guid TenantId,
    Guid? LeadId,
    Guid? AssignedAgentId,
    string TaskType) : IntegrationEventBase;
