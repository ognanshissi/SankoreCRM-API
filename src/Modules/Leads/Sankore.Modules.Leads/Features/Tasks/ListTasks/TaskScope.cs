namespace Sankore.Modules.Leads.Features.Tasks.ListTasks;

using System.Linq.Expressions;
using Sankore.Modules.Administration.PublicApi;
using Sankore.Modules.Leads.Domain;
using Sankore.Modules.Leads.Features.Tasks.GetTask;

/// <summary>Who sees which tasks and how a row reads, for the task list and its paged page.</summary>
internal static class TaskScope
{
    /// <summary>
    /// An empty team means nobody to supervise, not "everyone": the filter used to be skipped then, and
    /// a branch manager alone in their agency saw every task of the tenant. A super-user's team is
    /// already the whole tenant (<c>GetTeamAgentIdsAsync</c>).
    ///
    /// The supervisor is added back: <c>GetTeamAgentIdsAsync</c> answers "whom do I manage" and leaves
    /// the caller out, so a manager never saw the tasks assigned to THEM — on the lead's Tâches tab,
    /// which lists by lead only, their own first-contact task vanished.
    /// </summary>
    public static async Task<List<Guid>> TeamAsync(IAdministrationModule admin, Guid tenantId, Guid userId, CancellationToken ct)
    {
        var teamIds = await admin.GetTeamAgentIdsAsync(tenantId, userId, ct);
        return teamIds.Append(userId).Distinct().ToList();
    }

    public static readonly Expression<Func<CrmTask, CrmTaskDto>> ToDto = t => new CrmTaskDto(
        t.Id, t.TenantId, t.Type, t.Priority, t.Title,
        t.Description, t.LeadId, t.AssignedAgentId, t.Status,
        t.DueAt, t.SlaDeadline, t.CreatedAt, t.StartedAt,
        t.CompletedAt, t.TriggerEventType, t.TriggerEventId,
        t.EntityType, t.EntityId, t.CreatedBy, null);
}
