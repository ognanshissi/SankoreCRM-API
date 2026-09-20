using Sankore.Shared.Kernel;

namespace Sankore.Modules.Leads.Domain;

/// <summary>
/// Tenant-scoped configurable pipeline stage definition.
/// Replaces the hardcoded <see cref="PipelineStage"/> enum for tenants that need
/// custom sales funnel stages. The enum is kept for backward compatibility.
/// </summary>
public sealed class PipelineStageConfig : ITenant
{
    public Guid Id { get; private set; }
    public Guid TenantId { get; private set; }

    /// <summary>Unique code per tenant (e.g. "NEW", "CONTACT_ATTEMPTED"). Max 30 chars.</summary>
    public string Code { get; private set; } = default!;

    /// <summary>Human-readable label. Max 100 chars.</summary>
    public string Label { get; private set; } = default!;

    /// <summary>Optional description. Max 500 chars.</summary>
    public string? Description { get; private set; }

    /// <summary>Ordering position in the pipeline funnel UI.</summary>
    public int DisplayOrder { get; private set; }

    /// <summary>Hex color for UI display (e.g. "#FF5733"). Max 7 chars.</summary>
    public string? Color { get; private set; }

    /// <summary>Whether this stage is currently usable.</summary>
    public bool IsActive { get; private set; }

    /// <summary>System stages (seeded) cannot be deactivated.</summary>
    public bool IsSystem { get; private set; }

    /// <summary>Marks terminal stages (e.g. Converted, Lost) where leads stop progressing.</summary>
    public bool IsFinal { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    private PipelineStageConfig() { } // EF Core

    // ── Factory ─────────────────────────────────────────────────────────────

    public static PipelineStageConfig Create(
        Guid tenantId,
        string code,
        string label,
        int displayOrder,
        string? description = null,
        string? color = null,
        bool isFinal = false,
        bool isSystem = false)
    {
        if (string.IsNullOrWhiteSpace(code))
            throw new DomainException("Pipeline stage code is required.");
        if (string.IsNullOrWhiteSpace(label))
            throw new DomainException("Pipeline stage label is required.");

        return new PipelineStageConfig
        {
            Id           = Guid.NewGuid(),
            TenantId     = tenantId,
            Code         = code.Trim().ToUpperInvariant(),
            Label        = label.Trim(),
            Description  = description?.Trim(),
            DisplayOrder = displayOrder,
            Color        = color?.Trim(),
            IsActive     = true,
            IsSystem     = isSystem,
            IsFinal      = isFinal,
            CreatedAt    = DateTimeOffset.UtcNow,
        };
    }

    // ── Mutations ────────────────────────────────────────────────────────────

    public void Update(
        string label,
        string? description,
        int displayOrder,
        string? color,
        bool isFinal)
    {
        if (string.IsNullOrWhiteSpace(label))
            throw new DomainException("Pipeline stage label is required.");

        Label        = label.Trim();
        Description  = description?.Trim();
        DisplayOrder = displayOrder;
        Color        = color?.Trim();
        IsFinal      = isFinal;
    }

    public void Activate()
    {
        IsActive = true;
    }

    public Result Deactivate()
    {
        if (IsSystem)
            return Result.Fail("CANNOT_DEACTIVATE_SYSTEM_STAGE");

        IsActive = false;
        return Result.Ok();
    }
}
