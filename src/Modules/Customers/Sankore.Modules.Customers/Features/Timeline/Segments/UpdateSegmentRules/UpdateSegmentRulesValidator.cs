using Sankore.Modules.Kyc.PublicApi;
using Sankore.Shared.Kernel;

namespace Sankore.Modules.Customers.Features.Timeline.Segments.UpdateSegmentRules;

using FluentValidation;
using Sankore.Modules.Customers.Domain;

/// <summary>
/// Validates a rule set before it can silently reclassify clients.
///
/// The enum-name checks matter more than they look: a typo such as
/// <c>requiredKycStatus: "Validated"</c> (the real value is <c>Approved</c>) would produce a
/// rule that never matches anyone and is impossible to spot in a JSON blob.
/// </summary>
public sealed class UpdateSegmentRulesValidator : AbstractValidator<UpdateSegmentRulesCommand>
{
    private const int MaxRules = 100;

    public UpdateSegmentRulesValidator()
    {
        RuleFor(c => c.Rules)
            .NotNull()
            .Must(r => r.Count <= MaxRules)
            .WithMessage($"A tenant may define at most {MaxRules} segmentation rules.");

        RuleFor(c => c.Rules)
            .Must(r => r.Select(x => x.Code?.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).Count() == r.Count)
            .WithMessage("Rule codes must be unique.")
            .When(c => c.Rules is not null);

        RuleForEach(c => c.Rules).ChildRules(rule =>
        {
            rule.RuleFor(r => r.Code)
                .NotEmpty().MaximumLength(60);

            rule.RuleFor(r => r.SegmentCode)
                .NotEmpty().MaximumLength(30);

            rule.RuleFor(r => r.Priority)
                .GreaterThanOrEqualTo(0);

            rule.RuleFor(r => r.MinTenureDays)
                .GreaterThanOrEqualTo(0).When(r => r.MinTenureDays.HasValue);

            rule.RuleFor(r => r.MaxTenureDays)
                .GreaterThanOrEqualTo(0).When(r => r.MaxTenureDays.HasValue);

            rule.RuleFor(r => r)
                .Must(r => r.MinTenureDays is null || r.MaxTenureDays is null
                           || r.MinTenureDays <= r.MaxTenureDays)
                .WithMessage("minTenureDays must not exceed maxTenureDays.");

            rule.RuleFor(r => r.MinDaysSinceLastActivity)
                .GreaterThanOrEqualTo(0).When(r => r.MinDaysSinceLastActivity.HasValue);

            rule.RuleFor(r => r.MaxDaysSinceLastActivity)
                .GreaterThanOrEqualTo(0).When(r => r.MaxDaysSinceLastActivity.HasValue);

            rule.RuleFor(r => r)
                .Must(r => r.MinDaysSinceLastActivity is null || r.MaxDaysSinceLastActivity is null
                           || r.MinDaysSinceLastActivity <= r.MaxDaysSinceLastActivity)
                .WithMessage("minDaysSinceLastActivity must not exceed maxDaysSinceLastActivity.");

            rule.RuleFor(r => r.RequiredKycStatus)
                .Must(v => Enum.TryParse<KycStatus>(v, ignoreCase: true, out _))
                .WithMessage("requiredKycStatus must be one of: "
                             + "NotStarted, Pending, InProgress, Approved, Rejected, Expired.")
                .When(r => !string.IsNullOrWhiteSpace(r.RequiredKycStatus));

            rule.RuleFor(r => r.RequiredRiskLevel)
                .Must(v => Enum.TryParse<RiskLevel>(v, ignoreCase: true, out _))
                .WithMessage("requiredRiskLevel must be one of: Unknown, Low, Medium, High.")
                .When(r => !string.IsNullOrWhiteSpace(r.RequiredRiskLevel));
        });
    }
}
