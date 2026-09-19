namespace Sankore.Modules.Leads.Features.QualificationTemplates.UpdateQualificationTemplate;

using FluentValidation;
using Sankore.Modules.Leads.Domain;
using Sankore.Modules.Leads.Features.QualificationTemplates.CreateQualificationTemplate;

internal sealed class UpdateQualificationTemplateValidator
    : AbstractValidator<UpdateQualificationTemplateCommand>
{
    public UpdateQualificationTemplateValidator()
    {
        RuleFor(x => x.Name).NotEmpty().MaximumLength(200);
        RuleFor(x => x.Description).MaximumLength(1000).When(x => x.Description is not null);

        RuleFor(x => x)
            .Must(x => (x.Questions is { Count: > 0 }) || (x.Sections is { Count: > 0 }))
            .WithMessage("At least one question is required (via Questions or Sections).");

        RuleForEach(x => x.Questions).ChildRules(ValidateQuestion)
            .When(x => x.Questions is not null);

        RuleForEach(x => x.Sections).ChildRules(s =>
        {
            s.RuleFor(x => x.Title).NotEmpty().MaximumLength(200);
            s.RuleFor(x => x.Description).MaximumLength(1000).When(x => x.Description is not null);
            s.RuleForEach(x => x.Questions).ChildRules(ValidateQuestion)
                .When(x => x.Questions is not null);
        }).When(x => x.Sections is not null);
    }

    private static void ValidateQuestion(AbstractValidator<QuestionInput> q)
    {
        q.RuleFor(x => x.Label).NotEmpty().MaximumLength(500);
        q.RuleFor(x => x.HelpText).MaximumLength(1000).When(x => x.HelpText is not null);
        q.RuleFor(x => x.PlaceholderText).MaximumLength(200).When(x => x.PlaceholderText is not null);
        q.RuleFor(x => x.Weight).GreaterThan(0).WithMessage("Question weight must be greater than zero.");
        q.RuleFor(x => x.Options)
            .NotEmpty()
            .WithMessage("Options are required for SingleChoice and MultiChoice questions.")
            .When(x => x.Type is QuestionType.SingleChoice or QuestionType.MultiChoice);
        q.RuleFor(x => x.MaxValue)
            .GreaterThan(x => x.MinValue)
            .WithMessage("MaxValue must be greater than MinValue.")
            .When(x => x.MinValue.HasValue && x.MaxValue.HasValue);
    }
}
