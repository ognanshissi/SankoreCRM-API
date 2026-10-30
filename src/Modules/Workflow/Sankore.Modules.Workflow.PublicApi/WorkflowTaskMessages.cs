using Sankore.Shared.Kernel;

namespace Sankore.Modules.Workflow.PublicApi;

/// <summary>
/// A « Tâche manuelle » step opened: someone must do it. The Leads module, which owns the CRM tasks
/// (« Ma journée »), creates one about the record and links it back to the step through
/// <c>TriggerEventType = <see cref="TriggerType"/></c> and <c>TriggerEventId = InstanceStepId</c>.
/// Written to the Workflow outbox in the transaction that opened the step.
/// </summary>
/// <param name="RoleCode">The step's role — used only when the record has no agent of its own.</param>
/// <param name="DueInHours">The step's delay; the task's due date. Null = the default.</param>
public sealed record WorkflowTaskRequested(
    Guid TenantId,
    Guid InstanceId,
    Guid InstanceStepId,
    string StepName,
    string EntityType,
    Guid EntityId,
    string? RoleCode,
    int? DueInHours) : IntegrationEventBase
{
    public const string TriggerType = "workflow.step";
}

/// <summary>
/// The « Tâche manuelle » step closed without its task — approved or rejected by hand, instance
/// cancelled or timed out: the task still open in « Ma journée » is cancelled.
/// </summary>
public sealed record WorkflowTaskWithdrawn(Guid TenantId, Guid InstanceStepId) : IntegrationEventBase;

/// <summary>
/// The CRM task of a « Tâche manuelle » step was completed (the step is franchie, by the person who
/// did the task) or cancelled (the step stays open for a manual approval). Published by Leads.
/// </summary>
public sealed record WorkflowStepTaskClosed(
    Guid TenantId,
    Guid InstanceStepId,
    bool Completed,
    Guid? ActedByUserId,
    string TaskTitle) : IntegrationEventBase;

/// <summary>One line for an instance's history, under a step, from another module.</summary>
/// <param name="Code">The history code (<c>TASK_CREATED</c>…).</param>
/// <param name="Detail">One French sentence. No personal value.</param>
public sealed record WorkflowStepNote(
    Guid TenantId,
    Guid InstanceStepId,
    string Code,
    string Detail) : IntegrationEventBase;
