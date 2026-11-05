namespace Sankore.Modules.Leads.Features.Tasks.ListTasks;

using MediatR;
using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Administration.PublicApi;
using Sankore.Modules.Leads.Domain;
using Sankore.Modules.Leads.Features.Tasks.GetTask;
using Sankore.Modules.Leads.Features.TaskTypes;
using Sankore.Modules.Leads.Infrastructure;
using Sankore.Shared.Infrastructure.Auth;
using Sankore.Shared.Kernel;

/// <summary>The tabs of « Toutes les tâches ».</summary>
public enum TaskPageView { Open, Overdue, Done, Cancelled, All }

/// <param name="Open">Pending or in progress, overdue ones included.</param>
/// <param name="Overdue">Open past their deadline: the SLA when the task has one, else its due date.</param>
public sealed record TaskPageCountsDto(int Open, int Overdue, int Done, int Cancelled, int All);

public sealed record CrmTaskPageDto(
    IReadOnlyList<CrmTaskDto> Items, int TotalCount, int Page, int PageSize, TaskPageCountsDto Counts);

/// <summary>
/// « Toutes les tâches » (2026-11-05, product owner, who could not find where to see them): every
/// task the caller may see, whatever its date or status, one page at a time. « Ma journée » only
/// shows today and the open tasks ahead, and <c>GET tasks</c> has no paging.
/// </summary>
/// <param name="AgentId">One person's tasks; absent, the caller's whole perimeter.</param>
internal sealed record GetTaskPageQuery(
    TaskPageView View = TaskPageView.Open,
    Guid? AgentId = null,
    string? Search = null,
    int Page = 1,
    int PageSize = 25) : IRequest<Result<CrmTaskPageDto>>;

internal sealed class GetTaskPageHandler(
    LeadsDbContext db,
    ITenantContext tenant,
    ICurrentUser currentUser,
    IAdministrationModule admin,
    TaskTypeCatalog taskTypes,
    TimeProvider clock)
    : IRequestHandler<GetTaskPageQuery, Result<CrmTaskPageDto>>
{
    public async Task<Result<CrmTaskPageDto>> Handle(GetTaskPageQuery query, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var scoped = await ScopedAsync(query.AgentId, ct);

        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            var search = query.Search.Trim().ToLower();
            scoped = scoped.Where(t => t.Title.ToLower().Contains(search)
                || db.Leads.Any(l => l.Id == t.LeadId && l.FullName.ToLower().Contains(search)));
        }

        var byStatus = await scoped
            .GroupBy(t => t.Status)
            .Select(g => new { g.Key, Count = g.Count() })
            .ToDictionaryAsync(g => g.Key, g => g.Count, ct);
        var overdue = await Overdue(scoped, now).CountAsync(ct);
        var counts = new TaskPageCountsDto(
            byStatus.GetValueOrDefault(CrmTaskStatus.Pending) + byStatus.GetValueOrDefault(CrmTaskStatus.InProgress),
            overdue,
            byStatus.GetValueOrDefault(CrmTaskStatus.Completed),
            byStatus.GetValueOrDefault(CrmTaskStatus.Cancelled),
            byStatus.Values.Sum());

        // What is left to do reads soonest first; what is over, latest first.
        IOrderedQueryable<CrmTask> ordered = query.View switch
        {
            TaskPageView.Open => Open(scoped).OrderBy(t => t.DueAt),
            TaskPageView.Overdue => Overdue(scoped, now).OrderBy(t => t.DueAt),
            TaskPageView.Done => scoped.Where(t => t.Status == CrmTaskStatus.Completed).OrderByDescending(t => t.CompletedAt),
            TaskPageView.Cancelled => scoped.Where(t => t.Status == CrmTaskStatus.Cancelled).OrderByDescending(t => t.DueAt),
            _ => scoped.OrderByDescending(t => t.DueAt),
        };
        var total = query.View switch
        {
            TaskPageView.Open => counts.Open,
            TaskPageView.Overdue => counts.Overdue,
            TaskPageView.Done => counts.Done,
            TaskPageView.Cancelled => counts.Cancelled,
            _ => counts.All,
        };

        var page = query.Page < 1 ? 1 : query.Page;
        var size = query.PageSize < 1 ? 25 : query.PageSize > 100 ? 100 : query.PageSize;
        var items = await ordered
            .ThenBy(t => t.Id)
            .Skip((page - 1) * size)
            .Take(size)
            .Select(TaskScope.ToDto)
            .ToListAsync(ct);

        var labels = await taskTypes.LabelsAsync(ct);
        return Result.Ok(new CrmTaskPageDto(
            items.Select(t => t with { TypeLabel = labels.GetValueOrDefault(t.Type, t.Type) }).ToList(),
            total, page, size, counts));
    }

    /// <summary>
    /// An agent sees their own tasks; a manager their team's and their own, and nobody else's by naming
    /// them. Roles that see every agency also get the tasks nobody holds (a KYC circuit's, for one) —
    /// without them « toutes les tâches » would leave out exactly those that are waiting for someone.
    /// </summary>
    private async Task<IQueryable<CrmTask>> ScopedAsync(Guid? agentId, CancellationToken ct)
    {
        var q = db.CrmTasks.AsNoTracking();
        var me = currentUser.Id;
        if (!TaskScope.IsSupervisor(currentUser.Roles))
            return q.Where(t => t.AssignedAgentId == me);

        var team = await TaskScope.TeamAsync(admin, tenant.CurrentTenantId, me, ct);
        if (agentId is { } agent)
            return team.Contains(agent) ? q.Where(t => t.AssignedAgentId == agent) : q.Where(_ => false);

        return TaskScope.SeesEveryAgency(currentUser.Roles)
            ? q.Where(t => t.AssignedAgentId == null || team.Contains(t.AssignedAgentId.Value))
            : q.Where(t => t.AssignedAgentId != null && team.Contains(t.AssignedAgentId.Value));
    }

    private static IQueryable<CrmTask> Open(IQueryable<CrmTask> q) =>
        q.Where(t => t.Status == CrmTaskStatus.Pending || t.Status == CrmTaskStatus.InProgress);

    private static IQueryable<CrmTask> Overdue(IQueryable<CrmTask> q, DateTimeOffset now) =>
        Open(q).Where(t => (t.SlaDeadline ?? t.DueAt) < now);
}
