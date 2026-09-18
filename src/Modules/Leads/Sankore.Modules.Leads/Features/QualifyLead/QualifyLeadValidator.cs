namespace Sankore.Modules.Leads.Features.QualifyLead;

using FluentValidation;

internal sealed class QualifyLeadValidator : AbstractValidator<QualifyLeadCommand>
{
    public QualifyLeadValidator()
    {
        RuleFor(x => x.LeadId).NotEmpty();
        RuleFor(x => x.QualifiedBy).NotEmpty();
        RuleFor(x => x.TriggerEvent).NotEmpty().MaximumLength(100);

        When(x => x.Score.HasValue, () =>
            RuleFor(x => x.Score!.Value)
                .InclusiveBetween(0, 100)
                .WithMessage("Score must be between 0 and 100."));

        When(x => x.TemplateId.HasValue, () =>
            RuleFor(x => x.Answers)
                .NotNull()
                .WithMessage("Answers are required when a template is specified."));

        When(x => x.Answers is not null, () =>
            RuleForEach(x => x.Answers!).ChildRules(answer =>
            {
                answer.RuleFor(a => a.QuestionId).NotEmpty();
                answer.RuleFor(a => a.Value).NotNull().MaximumLength(500);
            }));
    }
}
