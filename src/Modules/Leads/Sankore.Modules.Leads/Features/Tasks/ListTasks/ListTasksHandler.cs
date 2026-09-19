namespace Sankore.Modules.Leads.Features.Tasks.ListTasks;

using MediatR;
using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Leads.Features.Tasks.GetTask;
using Sankore.Modules.Leads.Infrastructure;
using Sankore.Shared.Kernel;

internal sealed class ListTasksHandler(LeadsDbContext db)
    : IRequestHandler<ListTasksQuery, Result<IReadOnlyList<CrmTaskDto>>>
{
    public async Task<Result<IReadOnlyList<CrmTaskDto>>> Handle(
        ListTasksQuery query, CancellationToken ct)
    {
        var q = db.CrmTasks.AsQueryable();

        if (query.LeadId.HasValue)          q = q.Where(t => t.LeadId == query.LeadId);
        if (query.AssignedAgentId.HasValue) q = q.Where(t => t.AssignedAgentId == query.AssignedAgentId);
        if (query.Status.HasValue)          q = q.Where(t => t.Status == query.Status);
        if (query.Type.HasValue)            q = q.Where(t => t.Type == query.Type);

        var tasks = await q
            .OrderByDescending(t => t.DueAt)
            .Select(t => new CrmTaskDto(
                t.Id, t.TenantId, t.Type, t.Priority, t.Title,
                t.Description, t.LeadId, t.AssignedAgentId, t.Status,
                t.DueAt, t.SlaDeadline, t.CreatedAt, t.StartedAt,
                t.CompletedAt, t.TriggerEventType, t.TriggerEventId))
            .ToListAsync(ct);

        return Result.Ok<IReadOnlyList<CrmTaskDto>>(tasks);
    }
}
