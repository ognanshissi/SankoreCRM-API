namespace Sankore.Modules.Leads.Domain;

/// <summary>
/// Immutable record of a single dispatching decision. A Lead can have many
/// LeadAssignment rows over its lifetime (initial dispatch, reassignment
/// after refusal, escalation after SLA breach) — this is the audit trail
/// F13.14 requires ("dispatching manuel supervisé... tracé").
/// </summary>
public sealed class LeadAssignment
{
    public Guid Id { get; private set; }
    public Guid TenantId { get; private set; }
    public Guid LeadId { get; private set; }
    public Guid AgentId { get; private set; }
    public DispatchingStrategy Strategy { get; private set; }
    public double CompatibilityScore { get; private set; }
    public bool WasManualOverride { get; private set; }
    public string? OverrideReason { get; private set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; private set; } // When the assignment has done
    public DateTimeOffset SlaDeadline { get; private set; }
    public DateTimeOffset? FirstContactAt { get; private set; }

    private LeadAssignment() { } // EF Core

    public static LeadAssignment Create(
        Guid tenantId, Guid leadId, Guid agentId, DispatchingStrategy strategy,
        double compatibilityScore, DateTimeOffset slaDeadline, DateTimeOffset createdAt)
        => new()
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            LeadId = leadId,
            AgentId = agentId,
            Strategy = strategy,
            CompatibilityScore = compatibilityScore,
            WasManualOverride = false,
            CreatedAt = createdAt,
            SlaDeadline = slaDeadline
        };

    public static LeadAssignment CreateManualOverride(
        Guid tenantId, Guid leadId, Guid agentId, string reason, DateTimeOffset slaDeadline, DateTimeOffset createdAt)
        => new()
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            LeadId = leadId,
            AgentId = agentId,
            Strategy = DispatchingStrategy.CompatibilityScoring,
            CompatibilityScore = 0,
            WasManualOverride = true,
            OverrideReason = reason,
            CreatedAt = createdAt,
            SlaDeadline = slaDeadline
        };

    public void RecordFirstContact(DateTimeOffset at) => FirstContactAt ??= at;

    public bool HasBreachedSla(DateTimeOffset now) => FirstContactAt is null && now > SlaDeadline;
}
