namespace Sankore.Modules.Leads.Features.Opportunities.ProjectTasks;

using System.Transactions;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Administration.PublicApi;
using Sankore.Modules.Leads.Domain;
using Sankore.Modules.Leads.Features.Tasks.Collaboration;
using Sankore.Modules.Leads.Features.TaskTypes;
using Sankore.Modules.Leads.Features.Visibility;
using Sankore.Modules.Leads.Infrastructure;
using Sankore.Shared.Kernel;

/// <summary>
/// The tasks attached to a project (owner, 2026-10-27), for its page: open ones first, by due
/// date, then the closed ones, most recent first.
///
/// <para>
/// Two perimeters, both existing ones. The project must be one the caller can open — the same
/// predicate as its page. Each task must be one the caller may open — <see cref="TaskAccess"/>,
/// the one rule of every task endpoint: a colleague's task on the same project is listed to its
/// people and their managers, not to everybody who can see the project, so the list never shows a
/// row whose task would then answer « not found ».
/// </para>
/// </summary>
internal sealed record ListProjectTasksQuery(Guid OpportunityId) : IRequest<Result<IReadOnlyList<ProjectTaskDto>>>;

/// <param name="TypeLabel">The tenant's label for <paramref name="Type"/>.</param>
/// <param name="AssignedAgentName">The holder's name; null when nobody holds the task.</param>
public sealed record ProjectTaskDto(
    Guid Id,
    string Title,
    string Type,
    string TypeLabel,
    CrmTaskPriority Priority,
    CrmTaskStatus Status,
    DateTimeOffset DueAt,
    Guid? AssignedAgentId,
    string? AssignedAgentName,
    DateTimeOffset CreatedAt,
    DateTimeOffset? CompletedAt);

internal sealed class ListProjectTasksHandler(
    LeadsDbContext db,
    TaskAccess access,
    IAdministrationModule administration,
    ITenantContext tenant,
    TaskTypeCatalog taskTypes,
    LeadVisibility? visibility = null)
    : IRequestHandler<ListProjectTasksQuery, Result<IReadOnlyList<ProjectTaskDto>>>
{
    public async Task<Result<IReadOnlyList<ProjectTaskDto>>> Handle(ListProjectTasksQuery query, CancellationToken ct)
    {
        var projects = visibility?.Apply(db.Opportunities, db.Leads) ?? db.Opportunities;
        if (!await projects.AnyAsync(o => o.Id == query.OpportunityId, ct))
            return Result.Fail<IReadOnlyList<ProjectTaskDto>>(OpportunityErrors.NotFound);

        var tasks = await db.CrmTasks.AsNoTracking()
            .Where(t => t.EntityType == TaskEntityType.Project && t.EntityId == query.OpportunityId)
            .ToListAsync(ct);

        var visible = new List<CrmTask>(tasks.Count);
        foreach (var task in tasks)
            if (await access.CanAccessAsync(task, ct))
                visible.Add(task);

        var holders = visible.Select(t => t.AssignedAgentId).OfType<Guid>().Distinct().ToList();
        IReadOnlyDictionary<Guid, string> names = new Dictionary<Guid, string>();
        if (holders.Count > 0)
        {
            using var suppressed = new TransactionScope(TransactionScopeOption.Suppress, TransactionScopeAsyncFlowOption.Enabled);
            names = await administration.GetUserNamesAsync(tenant.CurrentTenantId, holders, ct);
        }

        var labels = await taskTypes.LabelsAsync(ct);
        var open = visible.Where(t => !t.IsClosed).OrderBy(t => t.DueAt);
        var closed = visible.Where(t => t.IsClosed).OrderByDescending(t => t.CompletedAt ?? t.DueAt);

        return Result.Ok<IReadOnlyList<ProjectTaskDto>>(open.Concat(closed)
            .Select(t => new ProjectTaskDto(
                t.Id, t.Title, t.Type, labels.GetValueOrDefault(t.Type, t.Type), t.Priority, t.Status, t.DueAt,
                t.AssignedAgentId,
                t.AssignedAgentId is { } holder ? names.GetValueOrDefault(holder) : null,
                t.CreatedAt, t.CompletedAt))
            .ToList());
    }
}
