using Sankore.Shared.Kernel;

namespace Sankore.Modules.Leads.Domain;

/// <summary>
/// Tenant-configurable weights and thresholds for the dispatching engine
/// (F13.10). Each IMF can tune these via module M12 Administration without
/// any code change or redeployment — this entity IS the configuration.
/// </summary>
public sealed class DispatchingRule: ITenant
{
    public Guid Id { get; private set; }
    public Guid TenantId { get; private set; }
    public string Name { get; private set; } = default!;
    public DispatchingStrategy Strategy { get; private set; }
    public ScoringWeights Weights { get; private set; } = default!;
    public int MaxLeadsPerAgent { get; private set; }

    /// <summary>
    /// Maximum number of open CRM tasks (Pending + InProgress) an agent may hold
    /// before being excluded from the dispatch pool (US-M13-082).
    /// </summary>
    public int MaxTasksPerAgent { get; private set; }

    /// <summary>
    /// How long (in minutes) a declining agent is excluded from the same task's
    /// re-dispatch pool (US-M13-084). Default: 30 min.
    /// </summary>
    public TimeSpan DeclineExclusionTtl { get; private set; }

    public int AntiMonopolyThreshold { get; private set; }
    public TimeSpan FirstContactSla { get; private set; }
    public bool IsActive { get; private set; }

    /// <summary>
    /// When multiple rules share the same strategy, the one with the highest
    /// priority wins. Default: 0.
    /// </summary>
    public int Priority { get; private set; }

    /// <summary>
    /// Agent IDs permanently excluded from this rule's dispatch pool
    /// (e.g. agents on leave, under probation, or dedicated to other segments).
    /// </summary>
    public IReadOnlyList<Guid> ExcludedAgentIds { get; private set; } = [];

    private DispatchingRule() { } // EF Core

    public static DispatchingRule Create(
        Guid tenantId, string name, DispatchingStrategy strategy,
        ScoringWeights weights, int maxLeadsPerAgent, int antiMonopolyThreshold,
        TimeSpan firstContactSla, int priority = 0,
        IReadOnlyList<Guid>? excludedAgentIds = null,
        int maxTasksPerAgent = 20,
        TimeSpan? declineExclusionTtl = null)
        => new()
        {
            Id                    = Guid.NewGuid(),
            TenantId              = tenantId,
            Name                  = name,
            Strategy              = strategy,
            Weights               = weights,
            MaxLeadsPerAgent      = maxLeadsPerAgent,
            MaxTasksPerAgent      = maxTasksPerAgent,
            DeclineExclusionTtl   = declineExclusionTtl ?? TimeSpan.FromMinutes(30),
            AntiMonopolyThreshold = antiMonopolyThreshold,
            FirstContactSla       = firstContactSla,
            IsActive              = true,
            Priority              = priority,
            ExcludedAgentIds      = excludedAgentIds ?? []
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
        MaxTasksPerAgent = 20,
        DeclineExclusionTtl = TimeSpan.FromMinutes(30),
        AntiMonopolyThreshold = 5,
        FirstContactSla = TimeSpan.FromHours(2),
        IsActive = true
    };

    public void Update(
        string name,
        ScoringWeights weights,
        int maxLeadsPerAgent,
        int maxTasksPerAgent,
        int antiMonopolyThreshold,
        TimeSpan firstContactSla,
        int priority,
        IReadOnlyList<Guid> excludedAgentIds,
        TimeSpan? declineExclusionTtl = null)
    {
        Name                  = name;
        Weights               = weights;
        MaxLeadsPerAgent      = maxLeadsPerAgent;
        MaxTasksPerAgent      = maxTasksPerAgent;
        DeclineExclusionTtl   = declineExclusionTtl ?? DeclineExclusionTtl;
        AntiMonopolyThreshold = antiMonopolyThreshold;
        FirstContactSla       = firstContactSla;
        Priority              = priority;
        ExcludedAgentIds      = excludedAgentIds;
    }

    public void Activate()   => IsActive = true;
    public void Deactivate() => IsActive = false;
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
    double Performance,
    double Agency = 0);
