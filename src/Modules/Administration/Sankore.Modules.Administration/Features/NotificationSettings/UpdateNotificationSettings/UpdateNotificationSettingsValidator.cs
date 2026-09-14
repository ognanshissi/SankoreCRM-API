using FluentValidation;
using Microsoft.Extensions.Localization;
using Sankore.Modules.Administration.Domain;
using Sankore.Modules.Administration.Resources;

namespace Sankore.Modules.Administration.Features.NotificationSettings.UpdateNotificationSettings;

internal sealed class UpdateNotificationSettingsValidator
    : AbstractValidator<UpdateNotificationSettingsCommand>
{
    public UpdateNotificationSettingsValidator(IStringLocalizer<AdministrationErrors> localizer)
    {
        RuleFor(x => x.ProviderType)
            .NotEmpty()
            .Must(p => TenantNotificationSettings.AllowedProviders.Contains(p))
            .WithMessage(_ => localizer["NotificationSettings.ProviderType.Invalid"]);

        When(x => x.ProviderType != "Default", () =>
        {
            RuleFor(x => x.FromEmail)
                .NotEmpty()
                .EmailAddress()
                .WithMessage(_ => localizer["NotificationSettings.FromEmail.Required"]);

            When(x => x.ProviderType == "Postmark", () =>
            {
                RuleFor(x => x.SendingDomain)
                    .NotEmpty()
                    .WithMessage(_ => localizer["NotificationSettings.SendingDomain.Required"]);
            });
        });

        RuleFor(x => x.ReplyToEmail)
            .EmailAddress()
            .When(x => !string.IsNullOrEmpty(x.ReplyToEmail))
            .WithMessage(_ => localizer["NotificationSettings.ReplyToEmail.Invalid"]);
    }
}
