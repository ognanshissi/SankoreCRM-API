using Sankore.Modules.Workflow.Domain;

namespace Sankore.Modules.Workflow.Features.Templates;

internal static class TemplateMapper
{
    public static WorkflowTemplateDto ToDto(this WorkflowTemplate t) =>
        new(
            Id: t.Id,
            TenantId: t.TenantId,
            EntityType: t.EntityType,
            Name: t.Name,
            Description: t.Description,
            IsActive: t.IsActive,
            CreatedAt: t.CreatedAt,
            UpdatedAt: t.UpdatedAt,
            Steps: t.Steps
                .OrderBy(s => s.Order)
                .Select(s => new WorkflowStepDto(
                    Id: s.Id,
                    Order: s.Order,
                    Name: s.Name,
                    Description: s.Description,
                    ApproverRoleCode: s.ApproverRoleCode,
                    TimeoutHours: s.TimeoutHours))
                .ToList());
}
