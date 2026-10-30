namespace Sankore.Modules.Workflow.Features.Instances;

public sealed record WorkflowInstanceDto(
    Guid Id,
    Guid TenantId,
    Guid TemplateId,
    string EntityType,
    Guid EntityId,
    string Status,
    int CurrentStepOrder,
    int TotalSteps,
    Guid StartedByUserId,
    DateTimeOffset StartedAt,
    DateTimeOffset? CompletedAt,
    List<WorkflowInstanceStepDto> Steps);

public sealed record WorkflowInstanceStepDto(
    Guid Id,
    int Order,
    string Name,
    string? ApproverRoleCode,
    string Status,
    Guid? ActedByUserId,
    string? Comment,
    DateTimeOffset CreatedAt,
    DateTimeOffset? CompletedAt,
    // The template step it was made from — the id the history entries point at — and its kind,
    // so the screen can tell an automatic notification from a validation.
    Guid StepDefinitionId,
    string Kind,
    // The step's own deadline — moved by an escalation — and who it is handed to, if anyone. The
    // screen used to recompute the deadline from the TEMPLATE, which agents cannot read.
    DateTimeOffset? DueAt,
    Guid? AssignedToUserId);
