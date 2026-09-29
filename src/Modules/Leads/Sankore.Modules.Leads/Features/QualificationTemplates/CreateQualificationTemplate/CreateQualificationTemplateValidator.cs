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
    }
}
