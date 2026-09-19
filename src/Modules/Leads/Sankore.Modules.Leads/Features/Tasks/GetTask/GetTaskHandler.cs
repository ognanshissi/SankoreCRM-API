namespace Sankore.Modules.Leads.Features.Tasks.GetTask;

using MediatR;
using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Leads.Infrastructure;
using Sankore.Shared.Kernel;

internal sealed class GetTaskHandler(LeadsDbContext db)
    : IRequestHandler<GetTaskQuery, Result<CrmTaskDto>>
{
    public async Task<Result<CrmTaskDto>> Handle(GetTaskQuery query, CancellationToken ct)
    {
        var task = await db.CrmTasks
            .FirstOrDefaultAsync(t => t.Id == query.TaskId, ct);

        if (task is null)
            return Result.Fail<CrmTaskDto>("TASK_NOT_FOUND");

        return Result.Ok(new CrmTaskDto(
            task.Id, task.TenantId, task.Type, task.Priority, task.Title,
            task.Description, task.LeadId, task.AssignedAgentId, task.Status,
            task.DueAt, task.SlaDeadline, task.CreatedAt, task.StartedAt,
            task.CompletedAt, task.TriggerEventType, task.TriggerEventId));
    }
}
