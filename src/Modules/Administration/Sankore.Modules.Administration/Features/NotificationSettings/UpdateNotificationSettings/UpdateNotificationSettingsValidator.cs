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

        // A tenant relay without a host is not a relay. The port is optional — the handler
        // derives 465 or 587 from the TLS mode rather than making an operator remember them.
        When(x => x.ProviderType == "Smtp", () =>
        {
            RuleFor(x => x.SmtpHost)
                .NotEmpty()
                .WithMessage(_ => localizer["NotificationSettings.SmtpHost.Required"]);

            RuleFor(x => x.SmtpPort)
                .InclusiveBetween(1, 65535)
                .When(x => x.SmtpPort.HasValue);

            RuleFor(x => x)
                .Must(x => !(x.SmtpUseSsl && x.SmtpUseStartTls))
                .WithMessage(_ => localizer["NotificationSettings.SmtpTls.Exclusive"]);
        });

        RuleFor(x => x.ReplyToEmail)
            .EmailAddress()
            .When(x => !string.IsNullOrEmpty(x.ReplyToEmail))
            .WithMessage(_ => localizer["NotificationSettings.ReplyToEmail.Invalid"]);
    }
}
