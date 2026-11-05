namespace Sankore.Modules.Leads.Features.Tasks.ListTasks;

using MediatR;
using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Administration.PublicApi;
using Sankore.Modules.Leads.Features.Tasks.GetTask;
using Sankore.Modules.Leads.Features.TaskTypes;
using Sankore.Modules.Leads.Infrastructure;
using Sankore.Shared.Infrastructure.Auth;
using Sankore.Shared.Kernel;

internal sealed class ListTasksHandler(
    LeadsDbContext db,
    ITenantContext tenant,
    IAdministrationModule admin,
    TaskTypeCatalog taskTypes)
    : IRequestHandler<ListTasksQuery, Result<IReadOnlyList<CrmTaskDto>>>
{
    public async Task<Result<IReadOnlyList<CrmTaskDto>>> Handle(
        ListTasksQuery query, CancellationToken ct)
    {
        var q = db.CrmTasks.AsQueryable();

        // ── Role-based scoping (US-M13-090) ─────────────────────────────
        // Supervisors see their team; agents see only their own tasks.
        if (query.CurrentUserId.HasValue && query.CurrentUserRoles is not null)
        {
            var isSupervisor = TeamSupervision.Holds(query.CurrentUserRoles, query.CurrentUserPermissions, Permissions.CanSuperviseTeam);

            if (!isSupervisor)
            {
                // Agent: restrict to own tasks only
                q = q.Where(t => t.AssignedAgentId == query.CurrentUserId.Value);
            }
            else
            {
                // Scoped to the team with or without an explicit agent: naming one outside it used
                // to read that agent's tasks, in any agency. It now reads empty, as GET tasks/page.
                var team = await TaskScope.TeamAsync(admin, tenant.CurrentTenantId, query.CurrentUserId.Value, ct);
                q = q.Where(t => t.AssignedAgentId != null && team.Contains(t.AssignedAgentId.Value));
            }
        }

        // ── Explicit filters ────────────────────────────────────────────
        if (query.LeadId.HasValue)          q = q.Where(t => t.LeadId == query.LeadId);
        if (query.AssignedAgentId.HasValue) q = q.Where(t => t.AssignedAgentId == query.AssignedAgentId);
        if (query.Status.HasValue)          q = q.Where(t => t.Status == query.Status);
        if (!string.IsNullOrWhiteSpace(query.Type)) q = q.Where(t => t.Type == query.Type);

        var tasks = await q
            .OrderByDescending(t => t.DueAt)
            .Select(TaskScope.ToDto)
            .ToListAsync(ct);

        var labels = await taskTypes.LabelsAsync(ct);
        return Result.Ok<IReadOnlyList<CrmTaskDto>>(
            tasks.Select(t => t with { TypeLabel = labels.GetValueOrDefault(t.Type, t.Type) }).ToList());
    }
}
