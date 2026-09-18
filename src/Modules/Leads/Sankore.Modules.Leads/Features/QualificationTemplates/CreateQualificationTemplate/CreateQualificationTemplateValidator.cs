namespace Sankore.Modules.Leads.Features.QualificationTemplates.CreateQualificationTemplate;

using FluentValidation;
using Sankore.Modules.Leads.Domain;

internal sealed class CreateQualificationTemplateValidator
    : AbstractValidator<CreateQualificationTemplateCommand>
{
    public CreateQualificationTemplateValidator()
    {
        RuleFor(x => x.Name).NotEmpty().MaximumLength(200);
        RuleFor(x => x.Description).MaximumLength(1000).When(x => x.Description is not null);
        RuleFor(x => x.ProductName).MaximumLength(100).When(x => x.ProductName is not null);

        RuleFor(x => x.Questions)
            .NotEmpty().WithMessage("At least one question is required.");

        RuleForEach(x => x.Questions).ChildRules(q =>
        {
            q.RuleFor(x => x.Label).NotEmpty().MaximumLength(500);
            q.RuleFor(x => x.Weight).GreaterThan(0).WithMessage("Question weight must be greater than zero.");
            q.RuleFor(x => x.Options)
                .NotEmpty()
                .WithMessage("Options are required for SingleChoice and MultiChoice questions.")
                .When(x => x.Type is QuestionType.SingleChoice or QuestionType.MultiChoice);
        });
    }
}
