namespace Sankore.Modules.Leads.Domain;

/// <summary>
/// Tenant-configurable weights and thresholds for the dispatching engine
/// (F13.10). Each IMF can tune these via module M12 Administration without
/// any code change or redeployment — this entity IS the configuration.
/// </summary>
public sealed class DispatchingRule
{
    public Guid Id { get; private set; }
    public Guid TenantId { get; private set; }
    public string Name { get; private set; } = default!;
    public DispatchingStrategy Strategy { get; private set; }
    public ScoringWeights Weights { get; private set; } = default!;
    public int MaxLeadsPerAgent { get; private set; }
    public int AntiMonopolyThreshold { get; private set; }
    public TimeSpan FirstContactSla { get; private set; }
    public bool IsActive { get; private set; }

    private DispatchingRule() { } // EF Core

    public static DispatchingRule Create(
        Guid tenantId, string name, DispatchingStrategy strategy,
        ScoringWeights weights, int maxLeadsPerAgent, int antiMonopolyThreshold,
        TimeSpan firstContactSla)
        => new()
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            Name = name,
            Strategy = strategy,
            Weights = weights,
            MaxLeadsPerAgent = maxLeadsPerAgent,
            AntiMonopolyThreshold = antiMonopolyThreshold,
            FirstContactSla = firstContactSla,
            IsActive = true
        };

    /// <summary>
    /// Sensible default used when a tenant has not configured its own rule
    /// yet — keeps the system usable out of the box while remaining fully
    /// overridable per IMF.
    /// </summary>
    public static DispatchingRule Default() => new()
    {
        Id = Guid.Empty,
        Name = "Default",
        Strategy = DispatchingStrategy.CompatibilityScoring,
        Weights = new ScoringWeights(Language: 25, Product: 25, Geography: 20, Workload: 15, Performance: 15),
        MaxLeadsPerAgent = 30,
        AntiMonopolyThreshold = 5,
        FirstContactSla = TimeSpan.FromHours(2),
        IsActive = true
    };
}

/// <summary>
/// Weights used by CompatibilityScorer; should sum to 100 by convention
/// (not strictly enforced, since a tenant might intentionally emphasize
/// one criterion — validated instead in the DispatchingRule admin UI).
/// </summary>
public sealed record ScoringWeights(
    double Language,
    double Product,
    double Geography,
    double Workload,
    double Performance);
