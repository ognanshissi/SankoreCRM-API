namespace Sankore.Modules.Leads.Features.ScoringConfigs;

public sealed record ScoringConfigDto(
    Guid Id,
    int Version,
    string Name,
    int QualificationThreshold,
    double WeightDemographics,
    double WeightEngagement,
    double WeightProduct,
    double WeightChannel,
    double WeightRecency,
    bool IsActive,
    DateTimeOffset CreatedAt,
    DateTimeOffset? ActivatedAt);
