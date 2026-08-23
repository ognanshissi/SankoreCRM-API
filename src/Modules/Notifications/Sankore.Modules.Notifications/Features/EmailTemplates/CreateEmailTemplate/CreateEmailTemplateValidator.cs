namespace Sankore.Modules.Notifications.Features.EmailTemplates.CreateEmailTemplate;

using FluentValidation;

internal sealed class CreateEmailTemplateValidator : AbstractValidator<CreateEmailTemplateCommand>
{
    public CreateEmailTemplateValidator()
    {
        RuleFor(x => x.TemplateKey).NotEmpty().MaximumLength(100);
        RuleFor(x => x.Locale).NotEmpty().MaximumLength(10);
        RuleFor(x => x.Subject).NotEmpty().MaximumLength(500);
        RuleFor(x => x.HtmlBody).NotEmpty();
    }
}
