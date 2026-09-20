namespace Sankore.Modules.Leads.Features.NurturingSequences.UpdateNurturingSequence;

using FluentValidation;

internal sealed class UpdateNurturingSequenceValidator
    : AbstractValidator<UpdateNurturingSequenceCommand>
{
    public UpdateNurturingSequenceValidator()
    {
        RuleFor(x => x.SequenceId).NotEmpty();
        RuleFor(x => x.Name).NotEmpty().MaximumLength(200);
        RuleFor(x => x.Description).MaximumLength(1000);
    }
}
