using FluentValidation;

namespace Sankore.Modules.Workflow.Features.Templates.Rules.AddRule;

internal sealed class AddRuleValidator : AbstractValidator<AddRuleCommand>
{
    public AddRuleValidator()
    {
        RuleFor(x => x.TemplateId).NotEmpty();
        RuleFor(x => x.StepId).NotEmpty();
        RuleFor(x => x.Field).NotEmpty().MaximumLength(200);
        RuleFor(x => x.Value).NotEmpty()
            .When(x => x.Operator is not
                (Domain.RuleOperator.IsEmpty or Domain.RuleOperator.IsNotEmpty));
        RuleFor(x => x.LogicalGroup).GreaterThanOrEqualTo(0);
    }
}
