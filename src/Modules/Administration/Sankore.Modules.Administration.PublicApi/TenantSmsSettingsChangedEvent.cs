using Sankore.Shared.Kernel;

namespace Sankore.Modules.Administration.PublicApi;

/// <summary>
/// Published through the Administration outbox when a tenant's SMS channel changes. Consumed by
/// M08 to drop its provider-resolution cache.
///
/// <para>
/// Its own event rather than reusing <see cref="TenantNotificationSettingsChangedEvent"/>: the two
/// invalidate different cache keys, and one event for both would make every email settings change
/// drop the SMS entry and the reverse — which is harmless today and is exactly the sort of
/// coupling that stops being harmless when one of the two resolutions gets expensive.
/// </para>
/// </summary>
public sealed record TenantSmsSettingsChangedEvent(Guid TenantId) : IIntegrationEvent
{
    public Guid EventId { get; init; } = Guid.NewGuid();
    public DateTimeOffset OccurredAt { get; init; } = DateTimeOffset.UtcNow;
}
