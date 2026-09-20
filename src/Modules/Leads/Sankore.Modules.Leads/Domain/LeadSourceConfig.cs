namespace Sankore.Modules.Leads.Domain;

using Sankore.Shared.Kernel;

/// <summary>
/// Tenant-scoped configurable lead source (US-M13-190).
/// Replaces the hardcoded <see cref="LeadSource"/> enum for new source management.
/// </summary>
public sealed class LeadSourceConfig : ITenant
{
    public Guid Id { get; private set; }
    public Guid TenantId { get; private set; }

    /// <summary>Short machine-readable code, unique per tenant (max 30 chars).</summary>
    public string Code { get; private set; } = default!;

    /// <summary>Human-readable display label (max 100 chars).</summary>
    public string Label { get; private set; } = default!;

    /// <summary>Optional description (max 500 chars).</summary>
    public string? Description { get; private set; }

    public bool IsActive { get; private set; }

    /// <summary>
    /// System sources (seeded from the original enum) cannot be deactivated or deleted.
    /// </summary>
    public bool IsSystem { get; private set; }

    /// <summary>Controls the ordering when presented in drop-downs or lists.</summary>
    public int DisplayOrder { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    private LeadSourceConfig() { }

    public static LeadSourceConfig Create(
        Guid tenantId,
        string code,
        string label,
        int displayOrder,
        string? description = null,
        bool isSystem = false)
        => new()
        {
            Id           = Guid.NewGuid(),
            TenantId     = tenantId,
            Code         = code,
            Label        = label,
            Description  = description,
            IsActive     = true,
            IsSystem     = isSystem,
            DisplayOrder = displayOrder,
            CreatedAt    = DateTimeOffset.UtcNow
        };

    public void Update(string code, string label, int displayOrder, string? description = null)
    {
        Code         = code;
        Label        = label;
        Description  = description;
        DisplayOrder = displayOrder;
    }

    public void Activate() => IsActive = true;

    public void Deactivate()
    {
        if (IsSystem)
            throw new DomainException("Cannot deactivate a system lead source.");

        IsActive = false;
    }
}
