namespace Sankore.Modules.Leads.Features.NurturingSequences.CreateNurturingSequence;

using FluentValidation;

internal sealed class CreateNurturingSequenceValidator
    : AbstractValidator<CreateNurturingSequenceCommand>
{
    public CreateNurturingSequenceValidator()
    {
        RuleFor(x => x.TenantId).NotEmpty();
        RuleFor(x => x.Name).NotEmpty().MaximumLength(200);
        RuleFor(x => x.Description).MaximumLength(1000);
        RuleFor(x => x.Steps).NotEmpty().WithMessage("At least one step is required.");
        RuleForEach(x => x.Steps).ChildRules(step =>
        {
            step.RuleFor(s => s.Order).GreaterThanOrEqualTo(0);
            step.RuleFor(s => s.EmailTemplateKey).NotEmpty().MaximumLength(200);
            step.RuleFor(s => s.Subject).MaximumLength(500);
        });
    }
}
