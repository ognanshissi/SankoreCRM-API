namespace Sankore.Modules.Leads.Domain;

/// <summary>
/// Immutable audit record of every owner change on a Lead.
/// Created atomically with each <see cref="Lead.SetOwner"/> call.
/// </summary>
public sealed class LeadOwnerAssignmentHistory
{
    public Guid Id { get; private set; }
    public Guid TenantId { get; private set; }
    public Guid LeadId { get; private set; }

    /// <summary>Previous owner; null when owner is set for the first time.</summary>
    public Guid? PreviousOwnerId { get; private set; }

    /// <summary>New owner after this assignment.</summary>
    public Guid NewOwnerId { get; private set; }

    /// <summary>How the assignment was triggered: Manual | Import | System.</summary>
    public string AssignmentMethod { get; private set; } = string.Empty;

    /// <summary>Free-text justification supplied by the manager (optional).</summary>
    public string? Reason { get; private set; }

    /// <summary>User who triggered the assignment (manager or system user).</summary>
    public Guid AssignedBy { get; private set; }

    public DateTimeOffset AssignedAt { get; private set; }

    private LeadOwnerAssignmentHistory() { }

    public static LeadOwnerAssignmentHistory Create(
        Guid tenantId,
        Guid leadId,
        Guid? previousOwnerId,
        Guid newOwnerId,
        string assignmentMethod,
        Guid assignedBy,
        string? reason = null)
        => new()
        {
            Id               = Guid.NewGuid(),
            TenantId         = tenantId,
            LeadId           = leadId,
            PreviousOwnerId  = previousOwnerId,
            NewOwnerId       = newOwnerId,
            AssignmentMethod = assignmentMethod,
            AssignedBy       = assignedBy,
            Reason           = reason?.Trim(),
            AssignedAt       = DateTimeOffset.UtcNow
        };
}
