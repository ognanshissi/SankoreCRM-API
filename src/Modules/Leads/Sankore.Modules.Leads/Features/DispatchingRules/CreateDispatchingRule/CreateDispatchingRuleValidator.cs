namespace Sankore.Modules.Leads.Features.DispatchingRules.CreateDispatchingRule;

using FluentValidation;

internal sealed class CreateDispatchingRuleValidator : AbstractValidator<CreateDispatchingRuleCommand>
{
    public CreateDispatchingRuleValidator()
    {
        RuleFor(x => x.Name).NotEmpty().MaximumLength(100);
        RuleFor(x => x.MaxLeadsPerAgent).GreaterThan(0);
        RuleFor(x => x.AntiMonopolyThreshold).GreaterThan(0);
        RuleFor(x => x.FirstContactSla).GreaterThan(TimeSpan.Zero);
        RuleFor(x => x.Weights.Language).GreaterThanOrEqualTo(0);
        RuleFor(x => x.Weights.Product).GreaterThanOrEqualTo(0);
        RuleFor(x => x.Weights.Geography).GreaterThanOrEqualTo(0);
        RuleFor(x => x.Weights.Workload).GreaterThanOrEqualTo(0);
        RuleFor(x => x.Weights.Performance).GreaterThanOrEqualTo(0);
    }
}
