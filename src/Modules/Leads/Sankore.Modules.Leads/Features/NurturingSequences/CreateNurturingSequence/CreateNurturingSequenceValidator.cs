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
        RuleFor(x => x.Steps).NotEmpty().WithMessage("Ajoutez au moins une étape.");
        RuleForEach(x => x.Steps).SetValidator(new StepInputValidator());
    }
}

/// <summary>One step, at creation as at update. The limits are the columns'.</summary>
internal sealed class StepInputValidator : AbstractValidator<StepInput>
{
    public StepInputValidator()
    {
        RuleFor(s => s.Order).GreaterThanOrEqualTo(0);
        RuleFor(s => s.DelayFromPrevious)
            .GreaterThanOrEqualTo(TimeSpan.Zero)
            .LessThanOrEqualTo(TimeSpan.FromDays(365))
            .WithMessage("Le délai d'une étape va de 0 à 365 jours.");
        RuleFor(s => s.EmailTemplateKey).NotEmpty().MaximumLength(100);
        RuleFor(s => s.Subject).MaximumLength(200);
    }
}
