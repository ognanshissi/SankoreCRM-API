using Sankore.Shared.Kernel;

namespace Sankore.Modules.Administration.PublicApi;

/// <summary>
/// Published (via Outbox) after a new user is created with PendingActivation status.
/// Consumed by the notification infrastructure to send the activation email.
/// Placed in PublicApi so it is accessible to other modules without
/// a dependency on the Administration domain assembly.
/// </summary>
public sealed record UserCreatedEvent(
    Guid TenantId,
    Guid UserId,
    string Email,
    string FullName) : IIntegrationEvent
{
    public Guid EventId { get; init; } = Guid.NewGuid();
    public DateTimeOffset OccurredAt { get; init; } = DateTimeOffset.UtcNow;
}