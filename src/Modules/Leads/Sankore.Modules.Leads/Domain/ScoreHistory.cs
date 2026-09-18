namespace Sankore.Modules.Leads.Domain;

/// <summary>
/// Immutable snapshot of a <see cref="Lead"/>'s score at a specific point in time.
/// Provides the full scoring audit trail required by M13-E06.
/// </summary>
public sealed class ScoreHistory
{
    public Guid Id { get; private set; }
    public Guid TenantId { get; private set; }
    public Guid LeadId { get; private set; }
    public int Score { get; private set; }

    /// <summary>JSON snapshot of the factor weights that produced this score.</summary>
    public string FactorsJson { get; private set; } = "{}";

    /// <summary>Business event that triggered the recalculation (e.g. "QUALIFICATION_COMPLETED").</summary>
    public string TriggerEvent { get; private set; } = string.Empty;

    public DateTimeOffset RecalculatedAt { get; private set; }

    /// <summary>Set when scoring was driven by a <see cref="QualificationResponse"/>; null for auto/manual scoring.</summary>
    public Guid? QualificationResponseId { get; private set; }

    private ScoreHistory() { }

    public static ScoreHistory Create(
        Guid tenantId, Guid leadId, int score, string triggerEvent,
        string factorsJson = "{}", Guid? qualificationResponseId = null)
        => new()
        {
            Id                       = Guid.NewGuid(),
            TenantId                 = tenantId,
            LeadId                   = leadId,
            Score                    = score,
            TriggerEvent             = triggerEvent,
            FactorsJson              = factorsJson,
            RecalculatedAt           = DateTimeOffset.UtcNow,
            QualificationResponseId  = qualificationResponseId
        };
}
