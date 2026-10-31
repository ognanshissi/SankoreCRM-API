namespace Sankore.Modules.Leads.Features.Tasks;

using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Leads.Domain;
using Sankore.Modules.Leads.Infrastructure;
using Sankore.Modules.Workflow.PublicApi;
using Sankore.Shared.Infrastructure.Messaging;

/// <summary>
/// An archived lead keeps no open task (2026-10-31, product owner): its « Premier contact »,
/// relances and the rest are cancelled with it, in the same unit of work — before, they stayed in
/// « Ma journée » of the agents, for a lead nobody works any more. Only tasks ABOUT the lead
/// (<see cref="CrmTask.LeadId"/>); a task about its project, client or KYC file is not the lead's.
///
/// <para>
/// The task of a workflow « Tâche manuelle » step is cancelled as <c>CancelTaskHandler</c> does:
/// the step is told and stays open for a manual decision. Nothing is saved here — the caller saves
/// with the lead — and the agents whose load changed are returned, for the capacity cache AFTER the
/// save.
/// </para>
/// </summary>
internal static class ArchivedLeadTasks
{
    private static readonly CrmTaskStatus[] Open = [CrmTaskStatus.Pending, CrmTaskStatus.InProgress];

    public static async Task<IReadOnlyList<(Guid TenantId, Guid AgentId)>> CancelOpenAsync(
        LeadsDbContext db, IEventPublisher? publisher, IReadOnlyCollection<Guid> leadIds, CancellationToken ct)
    {
        if (leadIds.Count == 0) return [];

        var tasks = await db.CrmTasks.AsTracking()
            .Where(t => t.LeadId != null && leadIds.Contains(t.LeadId.Value) && Open.Contains(t.Status))
            .ToListAsync(ct);

        foreach (var task in tasks)
        {
            if (task.Cancel().IsFailure) continue;
            if (publisher is not null && task.TriggerEventType == WorkflowTaskRequested.TriggerType && task.TriggerEventId is { } stepId)
                await publisher.PublishAsync(new WorkflowStepTaskClosed(task.TenantId, stepId, false, null, task.Title), ct);
        }

        return tasks.Where(t => t.AssignedAgentId.HasValue)
            .Select(t => (t.TenantId, t.AssignedAgentId!.Value))
            .Distinct()
            .ToList();
    }
}
