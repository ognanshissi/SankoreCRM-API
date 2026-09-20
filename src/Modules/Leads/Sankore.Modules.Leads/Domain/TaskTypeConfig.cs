namespace Sankore.Modules.Leads.Domain;

using Sankore.Shared.Kernel;

/// <summary>
/// Tenant-scoped configurable task type that replaces the hard-coded
/// <see cref="CrmTaskType"/> enum for classification purposes (US-M13-197).
/// System types are seeded at tenant provisioning and cannot be deactivated.
/// </summary>
public sealed class TaskTypeConfig : ITenant
{
    public Guid Id { get; private set; }
    public Guid TenantId { get; private set; }

    /// <summary>Unique machine-readable code within a tenant (e.g. "FIRST_CONTACT"). Max 30 chars.</summary>
    public string Code { get; private set; } = default!;

    /// <summary>Human-readable label shown in the UI. Max 100 chars.</summary>
    public string Label { get; private set; } = default!;

    /// <summary>Optional longer description. Max 500 chars.</summary>
    public string? Description { get; private set; }

    public bool IsActive { get; private set; }

    /// <summary>System types are seeded automatically and cannot be deactivated.</summary>
    public bool IsSystem { get; private set; }

    /// <summary>Controls the order in which types appear in dropdowns / lists.</summary>
    public int DisplayOrder { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    private TaskTypeConfig() { }

    public static TaskTypeConfig Create(
        Guid tenantId,
        string code,
        string label,
        string? description = null,
        bool isSystem = false,
        int displayOrder = 0)
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

    public void Update(string label, string? description, int displayOrder)
    {
        Label        = label;
        Description  = description;
        DisplayOrder = displayOrder;
    }

    public void Activate() => IsActive = true;

    public Result Deactivate()
    {
        if (IsSystem)
            return Result.Fail("CANNOT_DEACTIVATE_SYSTEM_TYPE");

        IsActive = false;
        return Result.Ok();
    }
}
