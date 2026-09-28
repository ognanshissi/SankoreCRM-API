namespace Sankore.Modules.Customers.Features.Timeline.Segments.GetSegmentRules;

using MediatR;
using Sankore.Modules.Customers.Domain;
using Sankore.Modules.Customers.Infrastructure;
using Sankore.Shared.Infrastructure.Auth;
using Sankore.Shared.Kernel;

internal sealed class GetSegmentRulesHandler(
    ICustomerSettings settings,
    ICurrentUser currentUser)
    : IRequestHandler<GetSegmentRulesQuery, Result<SegmentRulesDto>>
{
    public async Task<Result<SegmentRulesDto>> Handle(GetSegmentRulesQuery request, CancellationToken ct)
    {
        var json = await settings.GetStringAsync(
            currentUser.TenantId, CustomerSettingKeys.SegmentRulesJson, ct);

        if (!SegmentRuleEvaluation.TryParse(json, out var rules, out var parseError))
        {
            // Surfaced instead of thrown: an operator who broke the JSON needs to see WHY
            // from the screen that edits it, not a 500.
            return Result.Ok(new SegmentRulesDto([], 0, 0, IsValid: false, ParseError: parseError));
        }

        var dtos = rules
            .OrderBy(r => r.Priority)
            .Select(r => new SegmentRuleDto(
                r.Code,
                r.Priority,
                r.SegmentCode,
                r.MinTenureDays,
                r.MaxTenureDays,
                r.MaxDaysSinceLastActivity,
                r.MinDaysSinceLastActivity,
                r.RequiredKycStatus,
                r.RequiredRiskLevel,
                r.RequiresOutstandingData,
                r.IsEvaluable(),
                r.NotEvaluableReason()))
            .ToList();

        return Result.Ok(new SegmentRulesDto(
            dtos,
            EvaluableCount: dtos.Count(d => d.IsEvaluable),
            NotEvaluableCount: dtos.Count(d => !d.IsEvaluable),
            IsValid: true,
            ParseError: null));
    }
}
