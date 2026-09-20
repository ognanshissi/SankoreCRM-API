namespace Sankore.Modules.Leads.Features.Consumers;

using MassTransit;
using Microsoft.Extensions.Logging;
using Sankore.Modules.Administration.PublicApi;
using Sankore.Modules.Leads.Features.Tasks.Events;
using Sankore.Modules.Notifications.PublicApi;

/// <summary>
/// Sends an email notification to an agent when a CRM task is assigned to them.
/// Consumes <see cref="TaskAssignedIntegrationEvent"/> published by Assign/Dispatch/Reassign handlers.
/// Delivery via M08 (Notifications module) — never a direct SMTP call.
/// </summary>
public sealed class TaskAssignedNotificationConsumer(
    IAdministrationModule admin,
    INotificationsModule notif,
    ILogger<TaskAssignedNotificationConsumer> logger)
    : IConsumer<TaskAssignedIntegrationEvent>
{
    public async Task Consume(ConsumeContext<TaskAssignedIntegrationEvent> context)
    {
        var evt = context.Message;

        var agent = await admin.GetAgentAsync(evt.AgentId, context.CancellationToken);
        if (agent is null)
        {
            logger.LogWarning(
                "Cannot send task assignment notification: agent {AgentId} not found",
                evt.AgentId);
            return;
        }

        try
        {
            await notif.QueueEmailAsync(new QueueEmailRequest(
                TemplateKey:    "task.assigned",
                RecipientEmail: $"{agent.Id}@agent.internal",
                RecipientName:  agent.FullName,
                Module:         "Leads",
                Locale:         "fr",
                TemplateData: new Dictionary<string, object>
                {
                    ["agentName"] = agent.FullName,
                    ["taskTitle"] = evt.Title,
                    ["taskId"]    = evt.TaskId.ToString(),
                    ["dueAt"]     = evt.DueAt.ToString("g"),
                    ["leadId"]    = evt.LeadId?.ToString() ?? "",
                },
                IdempotencyKey: $"task-assigned-{evt.TaskId}-{evt.AgentId}",
                TenantId:       evt.TenantId), context.CancellationToken);

            logger.LogInformation(
                "Task assignment notification sent to agent {AgentId} for task {TaskId}",
                evt.AgentId, evt.TaskId);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex,
                "Failed to send task assignment notification for task {TaskId}",
                evt.TaskId);
        }
    }
}
