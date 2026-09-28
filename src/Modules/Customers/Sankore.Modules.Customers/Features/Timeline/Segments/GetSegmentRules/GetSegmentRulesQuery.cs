namespace Sankore.Modules.Customers.Features.Timeline.Segments.GetSegmentRules;

using MediatR;
using Sankore.Shared.Kernel;

/// <summary>Reads the tenant's segmentation rule set, in evaluation order (US-M01-BE-27).</summary>
public sealed record GetSegmentRulesQuery : IRequest<Result<SegmentRulesDto>>;

/// <param name="Rules">Rules by ascending priority — the exact order the nightly job walks.</param>
/// <param name="EvaluableCount">Rules that actually run today.</param>
/// <param name="NotEvaluableCount">Rules parked until M03/M04 expose outstanding data.</param>
/// <param name="IsValid">False when the stored JSON could not be parsed; see <paramref name="ParseError"/>.</param>
public sealed record SegmentRulesDto(
    IReadOnlyList<SegmentRuleDto> Rules,
    int EvaluableCount,
    int NotEvaluableCount,
    bool IsValid,
    string? ParseError);

/// <param name="IsEvaluable">
/// False when the rule depends on outstanding balances or product holdings — data owned by
/// M03 (Savings) / M04 (Credit), which expose no contract yet. Such a rule is stored and
/// listed but NEVER applied, so the UI must show it as inactive.
/// </param>
/// <param name="NotEvaluableReason">Machine-readable reason, e.g. <c>OUTSTANDING_DATA_UNAVAILABLE</c>.</param>
public sealed record SegmentRuleDto(
    string Code,
    int Priority,
    string SegmentCode,
    int? MinTenureDays,
    int? MaxTenureDays,
    int? MaxDaysSinceLastActivity,
    int? MinDaysSinceLastActivity,
    string? RequiredKycStatus,
    string? RequiredRiskLevel,
    bool RequiresOutstandingData,
    bool IsEvaluable,
    string? NotEvaluableReason);
