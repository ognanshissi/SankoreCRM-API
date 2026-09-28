namespace Sankore.Modules.Customers.Domain;

/// <summary>
/// Snapshot of a loyalty score computation. Append-only: the score shown on the client is the
/// latest snapshot, and the series explains how it moved.
/// <see cref="IsProvisional"/> is true while the client has too little history for the score
/// to be trusted.
/// </summary>
public sealed class ClientLoyaltyScore
{
    public Guid Id { get; private set; }
    public Guid TenantId { get; private set; }
    public Guid ClientId { get; private set; }
    public int Score { get; private set; }
    public bool IsProvisional { get; private set; }

    /// <summary>JSON detail per component (tenure, regularity, volume, products).</summary>
    public string BreakdownJson { get; private set; } = default!;

    public DateTimeOffset ComputedAt { get; private set; }

    private ClientLoyaltyScore() { } // EF Core

    public static ClientLoyaltyScore Record(
        Guid tenantId,
        Guid clientId,
        int score,
        bool isProvisional,
        string breakdownJson,
        DateTimeOffset computedAt) =>
        new()
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            ClientId = clientId,
            Score = Math.Clamp(score, 0, 100),
            IsProvisional = isProvisional,
            BreakdownJson = string.IsNullOrWhiteSpace(breakdownJson) ? "{}" : breakdownJson,
            ComputedAt = computedAt,
        };
}
