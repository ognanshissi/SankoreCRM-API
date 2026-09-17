using FluentValidation;

namespace Sankore.Modules.Workflow.Features.Templates.Transitions.AddTransition;

internal sealed class AddTransitionValidator : AbstractValidator<AddTransitionCommand>
{
    public AddTransitionValidator()
    {
        RuleFor(x => x.EventCode).NotEmpty().MaximumLength(50);
        RuleFor(x => x.Priority).GreaterThanOrEqualTo(0);
        RuleFor(x => x).Must(x => x.ToStateId.HasValue || x.ToTerminalStatus.HasValue)
            .WithMessage("Either ToStateId or ToTerminalStatus must be provided.");
    }
}
