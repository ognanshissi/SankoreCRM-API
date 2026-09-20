namespace Sankore.Modules.Notifications.Features.EmailTemplates.ForkEmailTemplate;

using FluentValidation;

internal sealed class ForkEmailTemplateValidator : AbstractValidator<ForkEmailTemplateCommand>
{
    public ForkEmailTemplateValidator()
    {
        RuleFor(x => x.SourceTemplateId).NotEmpty();
    }
}
