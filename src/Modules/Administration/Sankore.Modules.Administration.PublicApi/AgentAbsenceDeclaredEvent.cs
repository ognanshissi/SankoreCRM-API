using Sankore.Shared.Kernel;

namespace Sankore.Modules.Administration.PublicApi;

/// <summary>
/// Publié (via l'outbox) quand un agent déclare une absence, ou en modifie la période ou le
/// remplaçant. Le module Leads s'en sert pour transférer les tâches en cours au remplaçant quand
/// <see cref="TransferTasks"/> est vrai : l'Administration ne voit pas les tâches CRM, et ne doit
/// pas les voir.
/// </summary>
public sealed record AgentAbsenceDeclaredEvent(
    Guid TenantId,
    Guid AgentId,
    DateOnly From,
    DateOnly To,
    Guid? ReplacementUserId,
    bool TransferTasks) : IIntegrationEvent
{
    public Guid EventId { get; init; } = Guid.NewGuid();
    public DateTimeOffset OccurredAt { get; init; } = DateTimeOffset.UtcNow;
}
