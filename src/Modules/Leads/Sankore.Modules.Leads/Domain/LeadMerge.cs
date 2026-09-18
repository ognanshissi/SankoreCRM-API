namespace Sankore.Modules.Leads.Domain;

using Sankore.Shared.Kernel;

/// <summary>
/// Persisted audit record of a lead merge operation.
/// One record is created per merge and is associated with both the target and source leads.
/// </summary>
public sealed class LeadMerge : ITenant
{
    public Guid Id { get; private set; }
    public Guid TenantId { get; private set; }
    public Guid TargetLeadId { get; private set; }
    public Guid SourceLeadId { get; private set; }
    public Guid MergedBy { get; private set; }
    public DateTimeOffset MergedAt { get; private set; }
    /// <summary>
    /// Comma-separated list of field names whose values were taken from the source lead
    /// (e.g. "email,nationalId"). Empty when no overrides were applied.
    /// </summary>
    public string? OverriddenFields { get; private set; }

    private LeadMerge() { } // EF Core

    public static LeadMerge Create(
        Guid tenantId,
        Guid targetLeadId,
        Guid sourceLeadId,
        Guid mergedBy,
        TimeProvider clock,
        IReadOnlyList<string>? overriddenFields = null)
        => new()
        {
            Id               = Guid.NewGuid(),
            TenantId         = tenantId,
            TargetLeadId     = targetLeadId,
            SourceLeadId     = sourceLeadId,
            MergedBy         = mergedBy,
            MergedAt         = clock.GetUtcNow(),
            OverriddenFields = overriddenFields is { Count: > 0 }
                                   ? string.Join(",", overriddenFields)
                                   : null
        };
}
