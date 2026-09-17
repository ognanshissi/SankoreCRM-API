using MediatR;
using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Workflow.Features.Tasks.ListTasks;
using Sankore.Modules.Workflow.Infrastructure;
using Sankore.Shared.Kernel;

namespace Sankore.Modules.Workflow.Features.Tasks.GetTask;

internal sealed class GetTaskHandler(WorkflowDbContext db)
    : IRequestHandler<GetTaskQuery, Result<TaskDto>>
{
    public async Task<Result<TaskDto>> Handle(GetTaskQuery request, CancellationToken ct)
    {
        var task = await db.WorkflowTasks
            .Where(t => t.Id == request.TaskId && t.InstanceId == request.InstanceId)
            .Select(t => new TaskDto(
                t.Id,
                t.InstanceId,
                t.Title,
                t.Description,
                t.AssignedToUserId,
                t.AssignedRoleCode,
                t.Priority,
                t.Status,
                t.DueAt,
                t.CreatedAt,
                t.CompletedAt,
                t.CompletionComment))
            .FirstOrDefaultAsync(ct);

        return task is null
            ? Result.Fail<TaskDto>($"Task {request.TaskId} not found.")
            : Result.Ok(task);
    }
}
