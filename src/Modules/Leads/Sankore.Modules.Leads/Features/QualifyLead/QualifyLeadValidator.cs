namespace Sankore.Modules.Leads.Features.QualifyLead;

using FluentValidation;

internal sealed class QualifyLeadValidator : AbstractValidator<QualifyLeadCommand>
{
    public QualifyLeadValidator()
    {
        RuleFor(x => x.LeadId).NotEmpty();

        When(x => x.Score.HasValue, () =>
            RuleFor(x => x.Score!.Value)
                .InclusiveBetween(0, 100)
                .WithMessage("Score must be between 0 and 100."));

        RuleFor(x => x.TriggerEvent).NotEmpty().MaximumLength(100);
    }
}
