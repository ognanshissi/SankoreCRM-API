namespace Sankore.Modules.Leads.Features.NurturingSequences.UpdateNurturingSequence;

using FluentValidation;
using Sankore.Modules.Leads.Features.NurturingSequences.CreateNurturingSequence;

internal sealed class UpdateNurturingSequenceValidator
    : AbstractValidator<UpdateNurturingSequenceCommand>
{
    public UpdateNurturingSequenceValidator()
    {
        RuleFor(x => x.SequenceId).NotEmpty();
        RuleFor(x => x.Name).NotEmpty().MaximumLength(200);
        RuleFor(x => x.Description).MaximumLength(1000);
        RuleFor(x => x.Steps).NotEmpty().WithMessage("Ajoutez au moins une étape.");
        RuleForEach(x => x.Steps).SetValidator(new StepInputValidator());
    }
}
