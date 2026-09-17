using FluentValidation;

namespace Sankore.Modules.Workflow.Features.Templates.UpdateStep;

internal sealed class UpdateStepValidator : AbstractValidator<UpdateStepCommand>
{
    public UpdateStepValidator()
    {
        RuleFor(x => x.TemplateId).NotEmpty();
        RuleFor(x => x.StepId).NotEmpty();
        RuleFor(x => x.Name).NotEmpty().MaximumLength(200);
        RuleFor(x => x.TimeoutHours).GreaterThan(0).When(x => x.TimeoutHours.HasValue);
    }
}
