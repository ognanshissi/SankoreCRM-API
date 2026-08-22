using Sankore.Shared.Kernel;

namespace Sankore.Modules.Administration.Domain.Events;

/// <summary>
/// Published (via Outbox) when a user is logically deactivated (F12.1 — Scenario 3).
/// The Leads module subscribes to this event to automatically reassign the agent's
/// active leads, preserving the cross-module decoupling rule (no FK between modules).
/// </summary>
public sealed record UserDeactivatedEvent(
    Guid TenantId,
    Guid UserId) : IntegrationEventBase;
