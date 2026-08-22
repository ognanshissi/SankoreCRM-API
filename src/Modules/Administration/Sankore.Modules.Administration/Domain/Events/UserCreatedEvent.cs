using Sankore.Shared.Kernel;

namespace Sankore.Modules.Administration.Domain.Events;

/// <summary>
/// Published (via Outbox) after a new user is created with PendingActivation status.
/// Consumed by the notification infrastructure to send the activation email.
/// </summary>
public sealed record UserCreatedEvent(
    Guid TenantId,
    Guid UserId,
    string Email,
    string FullName) : IntegrationEventBase;
