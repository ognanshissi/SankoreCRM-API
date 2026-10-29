using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MassTransit;
using Sankore.Modules.Workflow.Domain;
using Sankore.Modules.Workflow.PublicApi;

namespace Sankore.Modules.Workflow.Infrastructure.Actions.Executors;

/// <summary>
/// The transition action « Notification »: tells the holders of a role in their bell.
/// Config shape: <c>{ "title": "...", "body": "...", "recipientRoleCode": "..." }</c>.
///
/// <para>
/// It used to publish a <c>WorkflowNotificationIntegrationEvent</c> that no module consumed, so the
/// action did nothing. It now goes through the « Notification » step's path
/// (<see cref="WorkflowNotificationRequested"/>, role audience, in-app), whose consumers already
/// know who holds a role in the record's agency.
/// </para>
/// </summary>
internal sealed class SendNotificationExecutor(IBus bus) : IActionExecutor
{
    public ActionType ActionType => ActionType.SendNotification;

    public async Task ExecuteAsync(WorkflowAction action, WorkflowContext context, CancellationToken ct)
    {
        var cfg = JsonSerializer.Deserialize<NotificationConfig>(action.ConfigJson,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
            ?? new NotificationConfig();

        if (string.IsNullOrWhiteSpace(cfg.RecipientRoleCode) || string.IsNullOrWhiteSpace(cfg.Body))
            return;

        await bus.Publish(new WorkflowNotificationRequested(
            TenantId:       context.TenantId,
            InstanceId:     context.InstanceId,
            // No step here: one id per (instance, action), so the queue keys still make a replayed
            // action notify once. The outcome finds no step to file under and is only logged.
            InstanceStepId: StableId(context.InstanceId, action.Id),
            StepName:       string.IsNullOrWhiteSpace(cfg.Title) ? "Notification" : cfg.Title,
            EntityType:     context.EntityType,
            EntityId:       context.EntityId,
            Channel:        "Email",
            Recipients:     [WorkflowNotificationRecipients.Role],
            RoleCodes:      [cfg.RecipientRoleCode],
            Subject:        cfg.Title,
            Message:        cfg.Body), ct);
    }

    private static Guid StableId(Guid instanceId, Guid actionId)
        => new(SHA256.HashData(Encoding.UTF8.GetBytes($"{instanceId}:{actionId}"))[..16]);

    private sealed record NotificationConfig(
        string Title = "",
        string Body = "",
        string? RecipientRoleCode = null);
}
