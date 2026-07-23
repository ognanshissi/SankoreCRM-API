namespace Sankore.Shared.Infrastructure.Messaging;

using Sankore.Shared.Kernel;

/// <summary>
/// Publishes integration events on behalf of a module. Modules depend only
/// on this interface, never on the message broker (RabbitMQ / Kafka)
/// directly, so unit tests can substitute a fake publisher and the broker
/// can be swapped later without touching module code.
/// </summary>
public interface IEventPublisher
{
    Task PublishAsync<TEvent>(TEvent @event, CancellationToken ct) where TEvent : IIntegrationEvent;
}
