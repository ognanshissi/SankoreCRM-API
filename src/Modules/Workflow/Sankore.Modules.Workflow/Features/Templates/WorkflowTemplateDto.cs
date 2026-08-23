namespace Sankore.Modules.Workflow.Features.Templates;

public sealed record WorkflowTemplateDto(
    Guid Id,
    Guid TenantId,
    string EntityType,
    string Name,
    string? Description,
    bool IsActive,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    List<WorkflowStepDto> Steps);

public sealed record WorkflowStepDto(
    Guid Id,
    int Order,
    string Name,
    string? Description,
    string? ApproverRoleCode,
    int? TimeoutHours);
