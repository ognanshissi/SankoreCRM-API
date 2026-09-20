namespace Sankore.Modules.Leads.Features.ScoringConfigs.CreateScoringConfig;

using FluentValidation;

internal sealed class CreateScoringConfigValidator : AbstractValidator<CreateScoringConfigCommand>
{
    public CreateScoringConfigValidator()
    {
        RuleFor(x => x.Name).NotEmpty().MaximumLength(100);
        RuleFor(x => x.QualificationThreshold).GreaterThan(0);
        RuleFor(x => x.WeightDemographics).GreaterThanOrEqualTo(0);
        RuleFor(x => x.WeightEngagement).GreaterThanOrEqualTo(0);
        RuleFor(x => x.WeightProduct).GreaterThanOrEqualTo(0);
        RuleFor(x => x.WeightChannel).GreaterThanOrEqualTo(0);
        RuleFor(x => x.WeightRecency).GreaterThanOrEqualTo(0);
    }
}
