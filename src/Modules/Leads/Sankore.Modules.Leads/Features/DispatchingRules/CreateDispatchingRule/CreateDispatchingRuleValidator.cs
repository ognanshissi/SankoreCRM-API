namespace Sankore.Modules.Leads.Features.DispatchingRules.CreateDispatchingRule;

using FluentValidation;
using Sankore.Modules.Leads.Domain;

internal sealed class CreateDispatchingRuleValidator : AbstractValidator<CreateDispatchingRuleCommand>
{
    private static readonly DispatchingStrategy[] WeightsRequiredStrategies =
        [DispatchingStrategy.CompatibilityScoring, DispatchingStrategy.CherryPicking, DispatchingStrategy.StickyAssignment];

    public CreateDispatchingRuleValidator()
    {
        RuleFor(x => x.Name).NotEmpty().MaximumLength(100);
        RuleFor(x => x.MaxLeadsPerAgent).GreaterThan(0);
        RuleFor(x => x.AntiMonopolyThreshold).GreaterThan(0);
        RuleFor(x => x.FirstContactSla).GreaterThan(TimeSpan.Zero);

        // Weights required only for scoring-based strategies
        When(x => WeightsRequiredStrategies.Contains(x.Strategy), () =>
        {
            RuleFor(x => x.Weights).NotNull()
                .WithMessage("Weights are required for the selected strategy.");
        });

        When(x => x.Weights is not null, () =>
        {
            RuleFor(x => x.Weights.Language).GreaterThanOrEqualTo(0);
            RuleFor(x => x.Weights.Product).GreaterThanOrEqualTo(0);
            RuleFor(x => x.Weights.Geography).GreaterThanOrEqualTo(0);
            RuleFor(x => x.Weights.Workload).GreaterThanOrEqualTo(0);
            RuleFor(x => x.Weights.Performance).GreaterThanOrEqualTo(0);
        });
    }
}
