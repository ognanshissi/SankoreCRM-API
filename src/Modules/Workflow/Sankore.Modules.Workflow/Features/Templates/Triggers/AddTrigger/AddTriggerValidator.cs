using FluentValidation;

namespace Sankore.Modules.Workflow.Features.Templates.Triggers.AddTrigger;

internal sealed class AddTriggerValidator : AbstractValidator<AddTriggerCommand>
{
    public AddTriggerValidator()
    {
        RuleFor(x => x.TemplateId).NotEmpty();
        RuleFor(x => x.EventName).NotEmpty().MaximumLength(100);
    }
}
