namespace Sankore.Modules.Administration.Domain;

using Sankore.Shared.Kernel;

/// <summary>
/// Tenant-scoped financial product definition (F12.4).
/// Supports Loan, Savings, Tontine categories with free-form JSON parameters.
/// </summary>
public sealed class ProductSpeciality
{
    public Guid Id { get; private set; }
    public Guid TenantId { get; private set; }
    public string Name { get; private set; } = string.Empty;
    public string Code { get; private set; } = string.Empty;
    public string? Description { get; private set; }
    public ProductCategory Category { get; private set; }

    /// <summary>
    /// Free-form JSON parameters specific to the product category
    /// (e.g. cycle duration, grace period, interest rate, minimum balance).
    /// Schema is category-dependent — validated by the consuming module (M04/M05).
    /// </summary>
    public string? ParametersJson { get; private set; }

    public bool IsActive { get; private set; }
    public DateOnly? EffectiveFrom { get; private set; }
    public DateOnly? EffectiveTo { get; private set; }

    public string? BusinessProductId { get; private set; }
    public string BusinessPlatformName { get; private set; } = string.Empty;

    public DateTimeOffset CreatedAt { get; private set; }

    private ProductSpeciality() { }

    public static ProductSpeciality Create(
        Guid tenantId,
        string name,
        string code,
        ProductCategory category,
        string? description = null,
        string? parametersJson = null,
        DateOnly? effectiveFrom = null)
        => new()
        {
            Id             = Guid.NewGuid(),
            TenantId       = tenantId,
            Name           = name,
            Code           = code.ToUpperInvariant(),
            Category       = category,
            Description    = description,
            ParametersJson = parametersJson,
            IsActive       = true,
            EffectiveFrom  = effectiveFrom ?? DateOnly.FromDateTime(DateTime.UtcNow),
            CreatedAt      = DateTimeOffset.UtcNow,
        };

    public void Update(
        string name,
        string? description,
        string? parametersJson,
        string? businessProductId,
        string? businessPlatformName)
    {
        Name                 = name;
        Description          = description;
        ParametersJson       = parametersJson;
        BusinessProductId    = businessProductId;
        BusinessPlatformName = businessPlatformName ?? string.Empty;
    }

    public void LinkToCbs(string businessPlatformName, string? businessProductId)
    {
        BusinessPlatformName = businessPlatformName;
        BusinessProductId = businessProductId;
    }

    /// <summary>
    /// Retires the product from the catalogue. Existing contracts are NOT affected —
    /// the product simply stops appearing in new simulations/offers.
    /// </summary>
    public Result Retire(DateOnly effectiveTo)
    {
        if (!IsActive)
            return Result.Fail("PRODUCT_ALREADY_RETIRED");

        EffectiveTo = effectiveTo;
        IsActive    = false;
        return Result.Ok();
    }

    public void Activate()
    {
        IsActive    = true;
        EffectiveTo = null;
    }
}
