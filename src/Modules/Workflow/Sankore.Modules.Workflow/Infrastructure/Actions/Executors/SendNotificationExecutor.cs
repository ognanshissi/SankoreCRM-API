using System.Text.Json;
using MassTransit;
using Sankore.Modules.Workflow.Domain;

namespace Sankore.Modules.Workflow.Infrastructure.Actions.Executors;

/// <summary>
/// Publishes a <see cref="WorkflowNotificationIntegrationEvent"/> to the message bus.
/// Config shape: <c>{ "title": "...", "body": "...", "recipientRoleCode": "..." }</c>
/// </summary>
internal sealed class SendNotificationExecutor(IBus bus) : IActionExecutor
{
    public ActionType ActionType => ActionType.SendNotification;

    public async Task ExecuteAsync(WorkflowAction action, WorkflowContext context, CancellationToken ct)
    {
        var cfg = JsonSerializer.Deserialize<NotificationConfig>(action.ConfigJson,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
            ?? new NotificationConfig();

        await bus.Publish(new WorkflowNotificationIntegrationEvent(
            EventId:           Guid.NewGuid(),
            TenantId:          context.TenantId,
            InstanceId:        context.InstanceId,
            EntityType:        context.EntityType,
            EntityId:          context.EntityId,
            Title:             cfg.Title,
            Body:              cfg.Body,
            RecipientRoleCode: cfg.RecipientRoleCode,
            OccurredAt:        DateTimeOffset.UtcNow), ct);
    }

    private sealed record NotificationConfig(
        string Title = "",
        string Body = "",
        string? RecipientRoleCode = null);
}

/// <summary>Integration event published when a workflow sends a notification.</summary>
public sealed record WorkflowNotificationIntegrationEvent(
    Guid EventId,
    Guid TenantId,
    Guid InstanceId,
    string EntityType,
    Guid EntityId,
    string Title,
    string Body,
    string? RecipientRoleCode,
    DateTimeOffset OccurredAt);
