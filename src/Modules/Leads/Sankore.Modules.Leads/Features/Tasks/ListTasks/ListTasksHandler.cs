namespace Sankore.Modules.Leads.Features.Tasks.ListTasks;

using MediatR;
using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Administration.PublicApi;
using Sankore.Modules.Leads.Features.Tasks.GetTask;
using Sankore.Modules.Leads.Infrastructure;
using Sankore.Shared.Kernel;

internal sealed class ListTasksHandler(
    LeadsDbContext db,
    ITenantContext tenant,
    IAdministrationModule admin)
    : IRequestHandler<ListTasksQuery, Result<IReadOnlyList<CrmTaskDto>>>
{
    private static readonly HashSet<string> SupervisorRoles =
        ["System", "Administrator", "SalesManager", "BranchManager"];

    public async Task<Result<IReadOnlyList<CrmTaskDto>>> Handle(
        ListTasksQuery query, CancellationToken ct)
    {
        var q = db.CrmTasks.AsQueryable();

        // ── Role-based scoping (US-M13-090) ─────────────────────────────
        // Supervisors see their team; agents see only their own tasks.
        if (query.CurrentUserId.HasValue && query.CurrentUserRoles is not null)
        {
            var isSupervisor = query.CurrentUserRoles.Any(r => SupervisorRoles.Contains(r));

            if (!isSupervisor)
            {
                // Agent: restrict to own tasks only
                q = q.Where(t => t.AssignedAgentId == query.CurrentUserId.Value);
            }
            else if (query.AssignedAgentId is null)
            {
                // Supervisor without explicit agent filter: scope to team
                var teamIds = await admin.GetTeamAgentIdsAsync(
                    tenant.CurrentTenantId, query.CurrentUserId.Value, ct);

                if (teamIds.Count > 0)
                    q = q.Where(t => t.AssignedAgentId != null && teamIds.Contains(t.AssignedAgentId.Value));
            }
        }

        // ── Explicit filters ────────────────────────────────────────────
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
