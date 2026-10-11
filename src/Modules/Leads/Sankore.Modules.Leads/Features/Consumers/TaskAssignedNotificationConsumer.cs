namespace Sankore.Modules.Leads.Features.Consumers;

using System.Globalization;
using MassTransit;
using Microsoft.Extensions.Logging;
using Sankore.Modules.Administration.PublicApi;
using Sankore.Modules.Leads.Features.Tasks.Events;

/// <summary>
/// Sends an email notification to an agent when a CRM task is assigned to them.
/// Consumes <see cref="TaskAssignedIntegrationEvent"/> published by Assign/Dispatch/Reassign handlers.
///
/// <para>
/// Addressed through <see cref="IAdministrationModule.NotifyUserAsync"/> rather than by queueing
/// in M08 directly, because M12 owns the agent's address and locale. Two defects are closed by
/// that change, and both were silent:
/// </para>
/// <list type="number">
///   <item>
///     the recipient was <c>{agentId}@agent.internal</c> — a synthetic address nobody receives.
///     <c>AgentSummary</c> carries no email, so there was nothing better to pass.
///   </item>
///   <item>
///     the template data was camelCase (<c>taskTitle</c>) while the seeded template reads
///     snake_case (<c>{{ task_title }}</c>), so every placeholder rendered empty. The same class
///     of mismatch M02's biometry client paid for: names that do not line up fail by producing
///     nothing, not by raising.
///   </item>
/// </list>
/// </summary>
public sealed class TaskAssignedNotificationConsumer(
    IAdministrationModule admin,
    ILogger<TaskAssignedNotificationConsumer> logger)
    : IConsumer<TaskAssignedIntegrationEvent>
{
    public const string TemplateKey = "task.assigned";

    public async Task Consume(ConsumeContext<TaskAssignedIntegrationEvent> context)
    {
        var evt = context.Message;

        // Never throws, and returns false for an unknown or address-less agent — so there is no
        // separate existence check to keep in step with the send.
        var queued = await admin.NotifyUserAsync(
            new NotifyUserRequest(
                TenantId: evt.TenantId,
                UserId: evt.AgentId,
                TemplateKey: TemplateKey,
                IdempotencyKey: $"task-assigned-{evt.TaskId}-{evt.AgentId}",
                TemplateData: new Dictionary<string, object>
                {
                    // Snake_case, matching the seeded template verbatim. The recipient's own name
                    // is NOT passed here: M12 adds full_name, and the template was moved onto it
                    // so that one variable names the recipient in every template of the platform.
                    ["task_title"] = evt.Title,
                    ["task_id"] = evt.TaskId.ToString(),
                    // Invariant culture: the value is rendered into a body, and the process
                    // culture is not the recipient's.
                    ["due_at"] = evt.DueAt.ToString("g", CultureInfo.InvariantCulture),
                    ["lead_id"] = evt.LeadId?.ToString() ?? string.Empty,
                }),
            context.CancellationToken);

        if (queued)
        {
            logger.LogInformation(
                "Task assignment notification queued for agent {AgentId}, task {TaskId}",
                evt.AgentId, evt.TaskId);
        }
        else
        {
            logger.LogWarning(
                "No task assignment notification sent for task {TaskId}: agent {AgentId} is "
                + "unknown or has no email address",
                evt.TaskId, evt.AgentId);
        }
    }
}
