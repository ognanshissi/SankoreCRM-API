namespace Sankore.Modules.Leads.Features.Consumers;

using MassTransit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Sankore.Modules.Leads.Domain;
using Sankore.Modules.Leads.Features.DispatchLead.Events;
using Sankore.Modules.Leads.Infrastructure;

/// <summary>
/// Generates CRM tasks when a lead is successfully dispatched to an agent.
/// Evaluates all active TaskGenerationRules for the "LeadDispatched" event
/// and creates one task per matching rule.
/// Note: no ITenantContext available in MassTransit consumers (no HTTP context);
/// uses IgnoreQueryFilters() + manual TenantId filter from the event payload.
/// </summary>
public sealed class LeadDispatchedTaskConsumer(
    LeadsDbContext db,
    ILogger<LeadDispatchedTaskConsumer> logger)
    : IConsumer<LeadDispatchedEvent>
{
    public async Task Consume(ConsumeContext<LeadDispatchedEvent> context)
    {
        var evt = context.Message;
        var now = DateTimeOffset.UtcNow;

        var rules = await db.TaskGenerationRules
            .IgnoreQueryFilters()
            .Where(r => r.TenantId == evt.TenantId
                     && r.TriggerEventType == TaskTriggerEvents.LeadDispatched
                     && r.IsActive)
            .ToListAsync(context.CancellationToken);

        if (rules.Count == 0) return;

        foreach (var rule in rules)
        {
            var title = rule.TitleTemplate.Replace("{LeadId}", evt.LeadId.ToString());

            var task = CrmTask.Create(
                tenantId:        evt.TenantId,
                type:            rule.TaskType,
                priority:        rule.Priority,
                title:           title,
                dueAt:           now.Add(rule.DueDuration),
                leadId:          evt.LeadId,
                assignedAgentId: evt.AgentId,
                slaDeadline:     now.Add(rule.SlaDuration),
                description:     rule.DescriptionTemplate?.Replace("{LeadId}", evt.LeadId.ToString()),
                triggerEventType: TaskTriggerEvents.LeadDispatched,
                triggerEventId:  evt.LeadId);

            db.CrmTasks.Add(task);
        }

        await db.SaveChangesAsync(context.CancellationToken);

        logger.LogInformation(
            "Generated {Count} task(s) for dispatched lead {LeadId} (tenant {TenantId})",
            rules.Count, evt.LeadId, evt.TenantId);
    }
}
