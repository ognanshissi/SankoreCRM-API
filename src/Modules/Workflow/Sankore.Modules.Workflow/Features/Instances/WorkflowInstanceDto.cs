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
    DateTimeOffset? CompletedAt);
