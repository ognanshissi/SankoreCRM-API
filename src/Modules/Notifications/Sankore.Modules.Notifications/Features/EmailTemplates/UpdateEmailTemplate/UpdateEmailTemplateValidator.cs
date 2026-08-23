namespace Sankore.Modules.Notifications.Features.EmailTemplates.UpdateEmailTemplate;

using FluentValidation;

internal sealed class UpdateEmailTemplateValidator : AbstractValidator<UpdateEmailTemplateCommand>
{
    public UpdateEmailTemplateValidator()
    {
        RuleFor(x => x.SourceTemplateId).NotEmpty();
        RuleFor(x => x.Subject).NotEmpty().MaximumLength(500);
        RuleFor(x => x.HtmlBody).NotEmpty();
    }
}
