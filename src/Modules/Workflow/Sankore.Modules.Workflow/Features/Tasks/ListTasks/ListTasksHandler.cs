using MediatR;
using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Workflow.Domain;
using Sankore.Modules.Workflow.Infrastructure;
using Sankore.Shared.Kernel;

namespace Sankore.Modules.Workflow.Features.Tasks.ListTasks;

internal sealed class ListTasksHandler(WorkflowDbContext db)
    : IRequestHandler<ListTasksQuery, Result<List<TaskDto>>>
{
    public async Task<Result<List<TaskDto>>> Handle(ListTasksQuery request, CancellationToken ct)
    {
        var instanceExists = await db.WorkflowInstances
            .AnyAsync(i => i.Id == request.InstanceId, ct);

        if (!instanceExists)
            return Result.Fail<List<TaskDto>>($"Instance {request.InstanceId} not found.");

        var tasks = await db.WorkflowTasks
            .Where(t => t.InstanceId == request.InstanceId)
            .OrderBy(t => t.CreatedAt)
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
            .ToListAsync(ct);

        return Result.Ok(tasks);
    }
}
