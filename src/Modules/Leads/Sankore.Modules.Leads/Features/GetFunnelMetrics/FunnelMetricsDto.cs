namespace Sankore.Modules.Leads.Features.GetFunnelMetrics;

public sealed record FunnelMetricsDto(
    int TotalCaptured,
    int Qualifying,
    int Qualified,
    int Assigned,
    int Converted,
    int Lost,
    int Disqualified,
    double QualificationRate,
    double ConversionRate,
    double LossRate,
    IReadOnlyList<StageMetric> ByPipelineStage);

public sealed record StageMetric(string Stage, int Count, double SharePercent);
