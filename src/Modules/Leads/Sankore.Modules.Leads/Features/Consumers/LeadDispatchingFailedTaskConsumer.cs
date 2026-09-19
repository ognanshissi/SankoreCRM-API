namespace Sankore.Modules.Leads.Features.Consumers;

using MassTransit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Sankore.Modules.Leads.Domain;
using Sankore.Modules.Leads.Features.DispatchLead.Events;
using Sankore.Modules.Leads.Infrastructure;

/// <summary>
/// Generates CRM tasks when lead dispatching fails (no agent available).
/// Evaluates all active TaskGenerationRules for the "LeadDispatchingFailed" event.
/// Note: no ITenantContext available in MassTransit consumers (no HTTP context);
/// uses IgnoreQueryFilters() + manual TenantId filter from the event payload.
/// </summary>
public sealed class LeadDispatchingFailedTaskConsumer(
    LeadsDbContext db,
    ILogger<LeadDispatchingFailedTaskConsumer> logger)
    : IConsumer<LeadDispatchingFailedEvent>
{
    public async Task Consume(ConsumeContext<LeadDispatchingFailedEvent> context)
    {
        var evt = context.Message;
        var now = DateTimeOffset.UtcNow;

        var rules = await db.TaskGenerationRules
            .IgnoreQueryFilters()
            .Where(r => r.TenantId == evt.TenantId
                     && r.TriggerEventType == TaskTriggerEvents.LeadDispatchingFailed
                     && r.IsActive)
            .ToListAsync(context.CancellationToken);

        if (rules.Count == 0) return;

        foreach (var rule in rules)
        {
            var title = rule.TitleTemplate.Replace("{LeadId}", evt.LeadId.ToString());

            var task = CrmTask.Create(
                tenantId:         evt.TenantId,
                type:             rule.TaskType,
                priority:         rule.Priority,
                title:            title,
                dueAt:            now.Add(rule.DueDuration),
                leadId:           evt.LeadId,
                slaDeadline:      now.Add(rule.SlaDuration),
                description:      rule.DescriptionTemplate?.Replace("{LeadId}", evt.LeadId.ToString()),
                triggerEventType: TaskTriggerEvents.LeadDispatchingFailed,
                triggerEventId:   evt.LeadId);

            db.CrmTasks.Add(task);
        }

        await db.SaveChangesAsync(context.CancellationToken);

        logger.LogInformation(
            "Generated {Count} task(s) for failed dispatch of lead {LeadId} (tenant {TenantId})",
            rules.Count, evt.LeadId, evt.TenantId);
    }
}
