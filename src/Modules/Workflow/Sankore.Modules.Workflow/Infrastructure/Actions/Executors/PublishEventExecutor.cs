using System.Text.Json;
using MassTransit;
using Sankore.Modules.Workflow.Domain;

namespace Sankore.Modules.Workflow.Infrastructure.Actions.Executors;

/// <summary>
/// Publishes a named integration event on the message bus.
/// Config shape: <c>{ "eventType": "LeadEscalated", "payload": { "reason": "SLA breach" } }</c>
/// Consumers subscribe to <see cref="WorkflowActionTriggeredEvent"/> and filter by EventType.
/// </summary>
internal sealed class PublishEventExecutor(IBus bus) : IActionExecutor
{
    public ActionType ActionType => ActionType.PublishEvent;

    public async Task ExecuteAsync(WorkflowAction action, WorkflowContext context, CancellationToken ct)
    {
        var cfg = JsonSerializer.Deserialize<PublishEventConfig>(action.ConfigJson,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
            ?? new PublishEventConfig();

        await bus.Publish(new WorkflowActionTriggeredEvent(
            EventId:    Guid.NewGuid(),
            TenantId:   context.TenantId,
            InstanceId: context.InstanceId,
            EntityType: context.EntityType,
            EntityId:   context.EntityId,
            EventType:  cfg.EventType,
            Payload:    cfg.Payload,
            OccurredAt: DateTimeOffset.UtcNow), ct);
    }

    private sealed record PublishEventConfig(
        string EventType = "",
        string Payload = "{}");
}

/// <summary>Generic integration event raised when a PublishEvent action fires.</summary>
public sealed record WorkflowActionTriggeredEvent(
    Guid EventId,
    Guid TenantId,
    Guid InstanceId,
    string EntityType,
    Guid EntityId,
    string EventType,
    string Payload,
    DateTimeOffset OccurredAt);
