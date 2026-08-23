using Sankore.Modules.Workflow.Domain;

namespace Sankore.Modules.Workflow.Features.Instances;

internal static class InstanceMapper
{
    public static WorkflowInstanceDto ToDto(this WorkflowInstance i) =>
        new(
            Id: i.Id,
            TenantId: i.TenantId,
            TemplateId: i.TemplateId,
            EntityType: i.EntityType,
            EntityId: i.EntityId,
            Status: i.Status.ToString(),
            CurrentStepOrder: i.CurrentStepOrder,
            TotalSteps: i.Steps.Count,
            StartedByUserId: i.StartedByUserId,
            StartedAt: i.StartedAt,
            CompletedAt: i.CompletedAt,
            Steps: i.Steps
                .OrderBy(s => s.Order)
                .Select(s => new WorkflowInstanceStepDto(
                    Id: s.Id,
                    Order: s.Order,
                    Name: s.Name,
                    ApproverRoleCode: s.ApproverRoleCode,
                    Status: s.Status.ToString(),
                    ActedByUserId: s.ActedByUserId,
                    Comment: s.Comment,
                    CreatedAt: s.CreatedAt,
                    CompletedAt: s.CompletedAt))
                .ToList());
}
