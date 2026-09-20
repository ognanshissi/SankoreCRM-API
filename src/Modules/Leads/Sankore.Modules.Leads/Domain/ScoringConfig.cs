using Sankore.Shared.Kernel;

namespace Sankore.Modules.Leads.Domain;

/// <summary>
/// Tenant-scoped, versioned scoring configuration that controls how lead
/// scores are calculated and when a lead is considered "Qualified".
/// Only one config may be active per tenant at any given time.
/// </summary>
public sealed class ScoringConfig : ITenant
{
    public Guid Id { get; private set; }
    public Guid TenantId { get; private set; }
    public int Version { get; private set; }
    public string Name { get; private set; } = default!;

    /// <summary>
    /// A lead whose total score reaches this threshold is considered Qualified.
    /// </summary>
    public int QualificationThreshold { get; private set; } = 60;

    public double WeightDemographics { get; private set; }
    public double WeightEngagement { get; private set; }
    public double WeightProduct { get; private set; }
    public double WeightChannel { get; private set; }
    public double WeightRecency { get; private set; }

    /// <summary>
    /// Only one config may be active per tenant at a time.
    /// </summary>
    public bool IsActive { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset? ActivatedAt { get; private set; }

    private ScoringConfig() { } // EF Core

    public static ScoringConfig Create(
        Guid tenantId,
        int version,
        string name,
        int qualificationThreshold,
        double weightDemographics,
        double weightEngagement,
        double weightProduct,
        double weightChannel,
        double weightRecency)
        => new()
        {
            Id                     = Guid.NewGuid(),
            TenantId               = tenantId,
            Version                = version,
            Name                   = name,
            QualificationThreshold = qualificationThreshold,
            WeightDemographics     = weightDemographics,
            WeightEngagement       = weightEngagement,
            WeightProduct          = weightProduct,
            WeightChannel          = weightChannel,
            WeightRecency          = weightRecency,
            IsActive               = false,
            CreatedAt              = DateTimeOffset.UtcNow
        };

    public void Update(
        string name,
        int qualificationThreshold,
        double weightDemographics,
        double weightEngagement,
        double weightProduct,
        double weightChannel,
        double weightRecency)
    {
        Name                   = name;
        QualificationThreshold = qualificationThreshold;
        WeightDemographics     = weightDemographics;
        WeightEngagement       = weightEngagement;
        WeightProduct          = weightProduct;
        WeightChannel          = weightChannel;
        WeightRecency          = weightRecency;
    }

    public void Activate()
    {
        IsActive    = true;
        ActivatedAt = DateTimeOffset.UtcNow;
    }

    public void Deactivate()
    {
        IsActive = false;
    }
}
