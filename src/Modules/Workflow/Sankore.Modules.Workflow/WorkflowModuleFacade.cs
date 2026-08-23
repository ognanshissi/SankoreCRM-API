using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Workflow.Domain;
using Sankore.Modules.Workflow.Infrastructure;
using Sankore.Modules.Workflow.PublicApi;
using Sankore.Shared.Kernel;

namespace Sankore.Modules.Workflow;

/// <summary>
/// Internal implementation of <see cref="IWorkflowModule"/>.
/// Exposed to the DI container as the concrete type behind the public contract.
/// </summary>
internal sealed class WorkflowModuleFacade(WorkflowDbContext db) : IWorkflowModule
{
    public async Task<Result<Guid>> StartWorkflowAsync(
        WorkflowStartRequest request,
        CancellationToken ct = default)
    {
        var template = await db.WorkflowTemplates
            .IgnoreQueryFilters()
            .Include(t => t.Steps)
            .Where(t => t.TenantId == request.TenantId
                     && t.EntityType == request.EntityType
                     && t.IsActive)
            .FirstOrDefaultAsync(ct);

        if (template is null)
            return Result.Fail<Guid>(
                $"No active workflow template found for entity type '{request.EntityType}'.");

        var instance = WorkflowInstance.Start(template, request.EntityId, request.StartedByUserId);

        db.WorkflowInstances.Add(instance);
        await db.SaveChangesAsync(ct);

        return Result.Ok(instance.Id);
    }

    public async Task<Result<WorkflowInstanceStatus>> GetInstanceStatusAsync(
        Guid instanceId,
        Guid tenantId,
        CancellationToken ct = default)
    {
        var instance = await db.WorkflowInstances
            .IgnoreQueryFilters()
            .Include(i => i.Steps)
            .FirstOrDefaultAsync(i => i.Id == instanceId && i.TenantId == tenantId, ct);

        if (instance is null)
            return Result.Fail<WorkflowInstanceStatus>("Workflow instance not found.");

        return Result.Ok(new WorkflowInstanceStatus(
            InstanceId: instance.Id,
            Status: instance.Status.ToString(),
            CurrentStepOrder: instance.CurrentStepOrder,
            TotalSteps: instance.Steps.Count,
            IsCompleted: instance.Status == WorkflowStatus.Completed));
    }
}
