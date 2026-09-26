namespace Sankore.Modules.Leads.Features.QualificationTemplates.DuplicateQualificationTemplate;

using FluentValidation;

internal sealed class DuplicateQualificationTemplateValidator
    : AbstractValidator<DuplicateQualificationTemplateCommand>
{
    public DuplicateQualificationTemplateValidator()
    {
        RuleFor(x => x.TemplateId).NotEmpty();

        // Null means "derive one from the source"; an explicitly supplied name must be usable.
        RuleFor(x => x.Name).NotEmpty().MaximumLength(200).When(x => x.Name is not null);
    }
}
