namespace Sankore.Modules.Leads.Features.Tasks.Collaboration;

using System.Transactions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Sankore.Modules.Administration.PublicApi;
using Sankore.Modules.Leads.Domain;
using Sankore.Modules.Leads.Infrastructure;
using Sankore.Shared.Infrastructure.Auth;
using Sankore.Shared.Kernel;

/// <summary>
/// Who may open a task and work in it — read it, comment, tick its criteria, attach files, tag it.
/// The ONE rule for every task endpoint:
///
/// <list type="bullet">
///   <item>the people of the task: its holder, its creator, the colleagues associated with it;</item>
///   <item>the holder's line manager (« le responsable », <c>ReportsToUserId</c>) and the head of
///         the holder's agency (« le chef d'agence ») — the two people who answer for the holder;</item>
///   <item>a branch manager whose agency tree contains the holder, the same perimeter as the team
///         view of « Ma journée »;</item>
///   <item>System, Administrator and SalesManager, the bypass roles of <c>LeadAccessGuard</c>.</item>
/// </list>
///
/// A task held by nobody falls back on its creator for the managers' rule. Anyone else gets
/// <c>TASK_NOT_FOUND</c> and not a 403: a task's existence is not something to confirm to a
/// stranger. The Administration lookups FAIL CLOSED, and run outside the caller's transaction —
/// their own DbContext would otherwise promote it to a distributed one.
/// </summary>
internal sealed class TaskAccess(
    LeadsDbContext db,
    ICurrentUser me,
    IAdministrationModule administration,
    ILogger<TaskAccess> logger)
{
    public const string NotFound = "TASK_NOT_FOUND";

    private static readonly string[] BypassRoles = [Roles.System.Code, Roles.Administrator.Code, Roles.SalesManager.Code];

    public bool HasBypassRole => me.Roles.Any(r => BypassRoles.Contains(r, StringComparer.Ordinal));

    /// <summary>The task when the caller may work in it, <see cref="NotFound"/> otherwise.</summary>
    public async Task<Result<CrmTask>> LoadAsync(Guid taskId, bool tracking, CancellationToken ct)
    {
        var query = tracking ? db.CrmTasks.AsTracking() : db.CrmTasks.AsNoTracking();
        var task = await query.FirstOrDefaultAsync(t => t.Id == taskId, ct);

        return task is not null && await CanAccessAsync(task, ct)
            ? Result.Ok(task)
            : Result.Fail<CrmTask>(NotFound);
    }

    public async Task<bool> CanAccessAsync(CrmTask task, CancellationToken ct)
    {
        if (HasBypassRole) return true;

        var userId = me.Id;
        if (task.AssignedAgentId == userId || task.CreatedBy == userId) return true;
        if (await db.CrmTaskParticipants.AnyAsync(p => p.TaskId == task.Id && p.UserId == userId, ct)) return true;

        var holder = task.AssignedAgentId ?? task.CreatedBy;
        if (holder is not { } holderId) return false;

        try
        {
            using var suppressed = new TransactionScope(TransactionScopeOption.Suppress, TransactionScopeAsyncFlowOption.Enabled);

            var supervisors = await administration.GetUserSupervisorsAsync(task.TenantId, holderId, ct);
            if (supervisors.LineManagerId == userId || supervisors.AgencyManagerId == userId) return true;

            if (me.Roles.Contains(Roles.BranchManager.Code, StringComparer.Ordinal))
            {
                var team = await administration.GetTeamAgentIdsAsync(task.TenantId, userId, ct);
                if (team.Contains(holderId)) return true;
            }

            return false;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Task {TaskId}: supervision lookup failed, access refused.", task.Id);
            return false;
        }
    }

    /// <summary>
    /// The people a change is announced to: holder, creator, associated colleagues — minus the
    /// actor. The managers are not told of every comment; they come when they want to.
    /// </summary>
    public async Task<IReadOnlyList<Guid>> AudienceAsync(CrmTask task, Guid actorId, CancellationToken ct)
    {
        var participants = await db.CrmTaskParticipants.Where(p => p.TaskId == task.Id).Select(p => p.UserId).ToListAsync(ct);
        return new[] { task.AssignedAgentId, task.CreatedBy }
            .Where(id => id.HasValue).Select(id => id!.Value)
            .Concat(participants)
            .Where(id => id != actorId && id != Guid.Empty)
            .Distinct()
            .ToList();
    }
}
