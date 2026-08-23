using Sankore.Shared.Kernel;

namespace Sankore.Modules.Administration.PublicApi;

/// <summary>
/// Published via the Administration outbox whenever tenant notification
/// settings change. Consumed by the Notifications module to invalidate
/// its provider-resolution cache.
/// Placed in PublicApi so it is accessible to other modules without
/// a dependency on the Administration domain assembly.
/// </summary>
public sealed record TenantNotificationSettingsChangedEvent(Guid TenantId) : IIntegrationEvent
{
    public Guid EventId { get; init; } = Guid.NewGuid();
    public DateTimeOffset OccurredAt { get; init; } = DateTimeOffset.UtcNow;
}
