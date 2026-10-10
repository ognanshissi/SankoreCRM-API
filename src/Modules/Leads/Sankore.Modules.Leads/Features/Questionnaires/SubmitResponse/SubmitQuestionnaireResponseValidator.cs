namespace Sankore.Modules.Leads.Features.Questionnaires.SubmitResponse;

using FluentValidation;

internal sealed class SubmitQuestionnaireResponseValidator
    : AbstractValidator<SubmitQuestionnaireResponseCommand>
{
    public SubmitQuestionnaireResponseValidator()
    {
        RuleFor(c => c.LeadId).NotEmpty();
        RuleFor(c => c.TemplateId).NotEmpty();
        RuleFor(c => c.SubjectType).NotEmpty();
        RuleFor(c => c.SubjectId).NotEmpty();

        RuleFor(c => c.Answers).NotEmpty()
            .WithMessage("At least one answer is required; an empty submission is not a declaration.");

        RuleForEach(c => c.Answers).ChildRules(a =>
        {
            a.RuleFor(x => x.QuestionId).NotEmpty();
            // No pattern and no length cap on the value: a free-text health answer is a sentence,
            // and the schema is the questionnaire's own business, not this validator's.
            a.RuleFor(x => x.Value).NotNull();
        });
    }
}
