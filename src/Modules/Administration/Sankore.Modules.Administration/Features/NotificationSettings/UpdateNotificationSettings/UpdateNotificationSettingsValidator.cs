using FluentValidation;
using Sankore.Modules.Administration.Domain;

namespace Sankore.Modules.Administration.Features.NotificationSettings.UpdateNotificationSettings;

internal sealed class UpdateNotificationSettingsValidator
    : AbstractValidator<UpdateNotificationSettingsCommand>
{
    public UpdateNotificationSettingsValidator()
    {
        RuleFor(x => x.ProviderType)
            .NotEmpty()
            .Must(p => TenantNotificationSettings.AllowedProviders.Contains(p))
            .WithMessage($"ProviderType must be one of: {string.Join(", ", TenantNotificationSettings.AllowedProviders)}");

        // Non-Default providers require a From address
        When(x => x.ProviderType != "Default", () =>
        {
            RuleFor(x => x.FromEmail)
                .NotEmpty()
                .EmailAddress()
                .WithMessage("FromEmail is required and must be a valid email address for non-default providers.");

            // Postmark requires a dedicated sending domain
            When(x => x.ProviderType == "Postmark", () =>
            {
                RuleFor(x => x.SendingDomain)
                    .NotEmpty()
                    .WithMessage("SendingDomain is required for Postmark.");
            });
        });

        RuleFor(x => x.ReplyToEmail)
            .EmailAddress()
            .When(x => !string.IsNullOrEmpty(x.ReplyToEmail))
            .WithMessage("ReplyToEmail must be a valid email address.");
    }
}
