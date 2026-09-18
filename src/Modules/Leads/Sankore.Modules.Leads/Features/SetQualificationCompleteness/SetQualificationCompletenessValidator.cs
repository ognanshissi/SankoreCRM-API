namespace Sankore.Modules.Leads.Features.SetQualificationCompleteness;

using FluentValidation;

internal sealed class SetQualificationCompletenessValidator
    : AbstractValidator<SetQualificationCompletenessCommand>
{
    public SetQualificationCompletenessValidator()
    {
        RuleFor(x => x.LeadId).NotEmpty();
        RuleFor(x => x.Completeness)
            .InclusiveBetween(0.0, 1.0)
            .WithMessage("Completeness must be between 0.0 and 1.0.");
    }
}
