using MediatR;
using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Workflow.Domain;
using Sankore.Modules.Workflow.Infrastructure;
using Sankore.Shared.Kernel;

namespace Sankore.Modules.Workflow.Features.Instances.GetCompletedTasks;

internal sealed class GetCompletedTasksHandler(WorkflowDbContext db)
    : IRequestHandler<GetCompletedTasksQuery, Result<PagedResult<CompletedTaskDto>>>
{
    public async Task<Result<PagedResult<CompletedTaskDto>>> Handle(
        GetCompletedTasksQuery request, CancellationToken ct)
    {
        var userId   = request.UserId;
        var page     = Math.Max(1, request.Page);
        var pageSize = request.PageSize is > 0 and <= 100 ? request.PageSize : 20;

        // Local variable — EF Core can capture this in the expression tree.
        // A static readonly field on the class is not reliably captured by EF's translator.
        var terminal = new[]
        {
            StepStatus.Approved,
            StepStatus.AutoApproved,
            StepStatus.Rejected,
            StepStatus.TimedOut,
            StepStatus.Skipped,
        };

        // A step counts as "completed by the user" when:
        //   a) They acted on it (approved / rejected), OR
        //   b) It was assigned to them when it reached a terminal state.
        var filtered = db.WorkflowInstanceSteps
            .Where(s => terminal.Contains(s.Status)
                     && (s.ActedByUserId == userId || s.AssignedToUserId == userId));

        var totalCount = await filtered.CountAsync(ct);

        // Build the full projection with joins BEFORE applying Skip/Take so EF
        // can generate a single translatable SQL query.
        var query =
            from s in filtered
            join i in db.WorkflowInstances  on s.InstanceId   equals i.Id
            join t in db.WorkflowTemplates  on i.TemplateId   equals t.Id
            orderby s.CompletedAt descending
            select new CompletedTaskDto(
                s.Id,
                s.InstanceId,
                t.Name,
                i.EntityType,
                i.EntityId,
                s.Name,
                s.Order,
                s.Status.ToString(),
                s.Comment,
                s.ActedByUserId,
                s.AssignedToUserId,
                s.CreatedAt,
                s.CompletedAt);

        var items = await query
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(ct);

        return Result.Ok(new PagedResult<CompletedTaskDto>(items, totalCount, page, pageSize));
    }
}
