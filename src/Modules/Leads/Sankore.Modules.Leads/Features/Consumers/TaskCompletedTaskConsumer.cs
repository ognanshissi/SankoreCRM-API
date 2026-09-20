namespace Sankore.Modules.Leads.Features.Consumers;

using MassTransit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Sankore.Modules.Leads.Domain;
using Sankore.Modules.Leads.Features.Tasks.CompleteTask;
using Sankore.Modules.Leads.Infrastructure;

/// <summary>
/// Generates follow-up CRM tasks when a task is completed (US-M13-093).
/// Evaluates all active TaskGenerationRules for the "TaskCompleted" event
/// and creates one task per matching rule.
/// References the completed task via the opaque EntityType/EntityId pattern
/// (TriggerEventType + TriggerEventId) — no physical FK across schemas.
/// </summary>
public sealed class TaskCompletedTaskConsumer(
    LeadsDbContext db,
    ILogger<TaskCompletedTaskConsumer> logger)
    : IConsumer<TaskCompletedEvent>
{
    public async Task Consume(ConsumeContext<TaskCompletedEvent> context)
    {
        var evt = context.Message;
        var now = DateTimeOffset.UtcNow;

        var rules = await db.TaskGenerationRules
            .IgnoreQueryFilters()
            .Where(r => r.TenantId == evt.TenantId
                     && r.TriggerEventType == TaskTriggerEvents.TaskCompleted
                     && r.IsActive)
            .ToListAsync(context.CancellationToken);

        if (rules.Count == 0) return;

        foreach (var rule in rules)
        {
            var title = rule.TitleTemplate
                .Replace("{LeadId}", evt.LeadId?.ToString() ?? "")
                .Replace("{TaskId}", evt.TaskId.ToString());

            var task = CrmTask.Create(
                tenantId:         evt.TenantId,
                type:             rule.TaskType,
                priority:         rule.Priority,
                title:            title,
                dueAt:            now.Add(rule.DueDuration),
                leadId:           evt.LeadId,
                assignedAgentId:  evt.AssignedAgentId,
                slaDeadline:      now.Add(rule.SlaDuration),
                description:      rule.DescriptionTemplate?
                                      .Replace("{LeadId}", evt.LeadId?.ToString() ?? "")
                                      .Replace("{TaskId}", evt.TaskId.ToString()),
                triggerEventType: TaskTriggerEvents.TaskCompleted,
                triggerEventId:   evt.TaskId);

            db.CrmTasks.Add(task);
        }

        await db.SaveChangesAsync(context.CancellationToken);

        logger.LogInformation(
            "Generated {Count} follow-up task(s) for completed task {TaskId} (tenant {TenantId})",
            rules.Count, evt.TaskId, evt.TenantId);
    }
}
