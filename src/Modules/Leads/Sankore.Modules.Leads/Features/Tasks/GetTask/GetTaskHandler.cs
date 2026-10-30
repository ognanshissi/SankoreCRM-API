namespace Sankore.Modules.Leads.Features.Tasks.GetTask;

using MediatR;
using Sankore.Modules.Leads.Features.Tasks.Collaboration;
using Sankore.Modules.Leads.Features.TaskTypes;
using Sankore.Shared.Kernel;

internal sealed class GetTaskHandler(TaskAccess access, TaskTypeCatalog taskTypes)
    : IRequestHandler<GetTaskQuery, Result<CrmTaskDto>>
{
    public async Task<Result<CrmTaskDto>> Handle(GetTaskQuery query, CancellationToken ct)
    {
        // Through TaskAccess: before it, any holder of lead:task:read could read any task by id.
        var loaded = await access.LoadAsync(query.TaskId, tracking: false, ct);
        if (loaded.IsFailure)
            return Result.Fail<CrmTaskDto>(loaded.Error!);
        var task = loaded.Value!;
        var labels = await taskTypes.LabelsAsync(ct);

        return Result.Ok(new CrmTaskDto(
            task.Id, task.TenantId, task.Type, task.Priority, task.Title,
            task.Description, task.LeadId, task.AssignedAgentId, task.Status,
            task.DueAt, task.SlaDeadline, task.CreatedAt, task.StartedAt,
            task.CompletedAt, task.TriggerEventType, task.TriggerEventId,
            task.EntityType, task.EntityId, task.CreatedBy,
            labels.GetValueOrDefault(task.Type, task.Type)));
    }
}
