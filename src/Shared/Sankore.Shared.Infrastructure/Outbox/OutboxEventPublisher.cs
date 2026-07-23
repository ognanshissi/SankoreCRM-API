namespace Sankore.Shared.Infrastructure.Outbox;

using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Sankore.Shared.Infrastructure.Messaging;
using Sankore.Shared.Kernel;

/// <summary>
/// Writes the integration event to the module's own outbox table via its
/// own DbContext, INSIDE the same DbContext instance the handler is already
/// using (so it lands in the same SaveChangesAsync/transaction). It does
/// NOT call SaveChangesAsync itself — the handler's own SaveChangesAsync
/// call persists the outbox row together with the aggregate change.
///
/// Registered once per module as:
///   services.AddScoped&lt;IEventPublisher, OutboxEventPublisher&lt;LeadsDbContext&gt;&gt;();
/// </summary>
public sealed class OutboxEventPublisher<TDbContext>(TDbContext db) : IEventPublisher
    where TDbContext : DbContext
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public Task PublishAsync<TEvent>(TEvent @event, CancellationToken ct)
        where TEvent : IIntegrationEvent
    {
        var payload = JsonSerializer.Serialize(@event, JsonOptions);

        var message = new OutboxMessage
        {
            Id = @event.EventId,
            EventType = typeof(TEvent).AssemblyQualifiedName!,
            PayloadJson = payload,
            OccurredAt = @event.OccurredAt
        };

        db.Set<OutboxMessage>().Add(message);

        // Intentionally NOT calling SaveChangesAsync here: the enclosing
        // handler's own SaveChangesAsync call commits this row atomically
        // together with the aggregate's state change.
        return Task.CompletedTask;
    }
}
