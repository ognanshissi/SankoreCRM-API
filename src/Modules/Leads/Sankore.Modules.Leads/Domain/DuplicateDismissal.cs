namespace Sankore.Modules.Leads.Domain;

using Sankore.Shared.Kernel;

/// <summary>
/// Records an agent's explicit decision that two leads are NOT duplicates.
/// Used to suppress the pair from future duplicate search results and
/// to provide an audit trail of duplicate review decisions.
/// </summary>
public sealed class DuplicateDismissal : ITenant
{
    public Guid Id { get; private set; }
    public Guid TenantId { get; private set; }
    /// <summary>The lead from whose context the duplicate search was performed.</summary>
    public Guid LeadId { get; private set; }
    /// <summary>The candidate lead that was reviewed and dismissed.</summary>
    public Guid CandidateLeadId { get; private set; }
    public Guid DismissedBy { get; private set; }
    public DateTimeOffset DismissedAt { get; private set; }
    /// <summary>Optional free-text justification provided by the agent.</summary>
    public string? Reason { get; private set; }

    private DuplicateDismissal() { } // EF Core

    public static DuplicateDismissal Create(
        Guid tenantId,
        Guid leadId,
        Guid candidateLeadId,
        Guid dismissedBy,
        TimeProvider clock,
        string? reason = null)
        => new()
        {
            Id              = Guid.NewGuid(),
            TenantId        = tenantId,
            LeadId          = leadId,
            CandidateLeadId = candidateLeadId,
            DismissedBy     = dismissedBy,
            DismissedAt     = clock.GetUtcNow(),
            Reason          = reason?.Trim()
        };
}
