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

    /// <summary>
    /// Values of the tenant's custom product fields, keyed by field key (dynamic forms, lot 1),
    /// checked against the product form by <c>ProductFormGate</c>. Distinct from
    /// <see cref="ParametersJson"/>, which is free-form and validated by nobody.
    /// </summary>
    public string? CustomFieldsJson { get; private set; }

    public bool IsActive { get; private set; }
    public DateOnly? EffectiveFrom { get; private set; }
    public DateOnly? EffectiveTo { get; private set; }

    public string? BusinessProductId { get; private set; }
    public string BusinessPlatformName { get; private set; } = string.Empty;

    /// <summary>
    /// What this line asks of the household and of the people designated on it (BNF-B-06). Never
    /// null: a line nobody has configured carries <see cref="BeneficiaryPolicy.None"/>, which is
    /// the most restrictive reading — no household, no designation, nothing required.
    /// </summary>
    public BeneficiaryPolicy BeneficiaryPolicy { get; private set; } = BeneficiaryPolicy.None;

    /// <summary>
    /// Whether a customer may hold this product more than once (BNF-B-22).
    ///
    /// <para>
    /// A real column and NOT part of <see cref="BeneficiaryPolicy"/>, deliberately: a rule about
    /// how many times a product may be held is not a rule about who is designated on it, and this
    /// one is QUERIED — the duplicate-request warning of BNF-B-22 reads it per line. Everything
    /// inside the policy is read whole and never filtered on, which is why that is jsonb and this
    /// is not.
    /// </para>
    ///
    /// <para>
    /// True by default. A savings account, a tontine or a credit may legitimately be held several
    /// times over; it is the exception — one health policy per person — that an administrator has
    /// to declare, and defaulting to false would refuse perfectly ordinary second subscriptions
    /// on every line until somebody noticed.
    /// </para>
    /// </summary>
    public bool AllowMultipleHoldings { get; private set; } = true;

    /// <summary>
    /// Whether this line may be KYC'd at a distance after a conversion, and how such a file is
    /// validated (KRM-B-01). Never null: an unconfigured line carries
    /// <see cref="KycRemotePolicy.Disabled"/>, which sends no link at all.
    /// </summary>
    public KycRemotePolicy KycRemotePolicy { get; private set; } = KycRemotePolicy.Disabled;

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
            BeneficiaryPolicy = BeneficiaryPolicy.None,
            AllowMultipleHoldings = true,
            KycRemotePolicy = KycRemotePolicy.Disabled,
        };

    /// <summary>Stores custom-field values already validated against the product form.</summary>
    public void SetCustomFields(string? json) => CustomFieldsJson = json;

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

    /// <summary>
    /// Replaces the line's beneficiary policy (BNF-B-06). A full replacement rather than a patch:
    /// the fields constrain each other — a child ceiling means nothing without a household, a
    /// share rule means nothing without the role it belongs to — so there is no setter that can
    /// leave the policy half-applied.
    /// </summary>
    public void SetBeneficiaryPolicy(BeneficiaryPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(policy);
        BeneficiaryPolicy = policy;
    }

    public void SetAllowMultipleHoldings(bool allow) => AllowMultipleHoldings = allow;

    /// <summary>
    /// Replaces the line's remote-KYC policy (KRM-B-01). A full replacement for the same reason
    /// <see cref="SetBeneficiaryPolicy"/> is one: the fields constrain each other — an automatic
    /// mode means nothing without a score floor, a required document kind means nothing on a line
    /// that collects nothing — so there is no setter that can leave it half-applied.
    /// </summary>
    public void SetKycRemotePolicy(KycRemotePolicy policy)
    {
        ArgumentNullException.ThrowIfNull(policy);
        KycRemotePolicy = policy;
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
