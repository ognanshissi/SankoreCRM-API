using FluentValidation;

namespace Sankore.Modules.Workflow.Features.Templates.Transitions.AddAction;

internal sealed class AddActionValidator : AbstractValidator<AddActionCommand>
{
    public AddActionValidator()
    {
        RuleFor(x => x.ConfigJson).NotEmpty();
        RuleFor(x => x.ExecutionOrder).GreaterThanOrEqualTo(0);
    }
}
