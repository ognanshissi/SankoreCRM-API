namespace Sankore.Modules.Customers.Domain;

/// <summary>
/// Append-only audit of every <see cref="Client"/> status transition.
/// <see cref="OldStatus"/> is null for the row written at creation time.
/// </summary>
public sealed class ClientStatusHistory
{
    public Guid Id { get; private set; }
    public Guid TenantId { get; private set; }
    public Guid ClientId { get; private set; }
    public ClientStatus? OldStatus { get; private set; }
    public ClientStatus NewStatus { get; private set; }
    public string? Reason { get; private set; }
    public Guid ActorUserId { get; private set; }
    public DateTimeOffset OccurredAt { get; private set; }

    private ClientStatusHistory() { } // EF Core

    internal static ClientStatusHistory Create(
        Guid tenantId,
        Guid clientId,
        ClientStatus? oldStatus,
        ClientStatus newStatus,
        string? reason,
        Guid actorUserId,
        DateTimeOffset occurredAt) =>
        new()
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            ClientId = clientId,
            OldStatus = oldStatus,
            NewStatus = newStatus,
            Reason = string.IsNullOrWhiteSpace(reason) ? null : reason.Trim(),
            ActorUserId = actorUserId,
            OccurredAt = occurredAt,
        };
}
