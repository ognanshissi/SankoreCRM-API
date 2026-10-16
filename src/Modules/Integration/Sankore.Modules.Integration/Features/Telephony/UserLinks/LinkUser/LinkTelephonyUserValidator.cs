namespace Sankore.Modules.Integration.Features.Telephony.UserLinks.LinkUser;

using FluentValidation;
using Sankore.Modules.Integration.Features.Telephony;

internal sealed class LinkTelephonyUserValidator : AbstractValidator<LinkTelephonyUserCommand>
{
    public LinkTelephonyUserValidator()
    {
        RuleFor(x => x.CrmUserId)
            .NotEmpty()
            .OverridePropertyName("crmUserId")
            .WithMessage("crmUserId is required.");

        RuleFor(x => x.ExternalUserId)
            .NotEmpty()
            .MaximumLength(100)
            .OverridePropertyName("externalUserId")
            .WithMessage("externalUserId is required and cannot exceed 100 characters.");

        RuleFor(x => x.ExternalEmail)
            .EmailAddress()
            .MaximumLength(320)
            .When(x => !string.IsNullOrWhiteSpace(x.ExternalEmail))
            .OverridePropertyName("externalEmail");

        RuleFor(x => x.DefaultCallerNumber)
            .Must(CallerNumber.IsWellFormed)
            .When(x => !string.IsNullOrWhiteSpace(x.DefaultCallerNumber))
            .OverridePropertyName("defaultCallerNumber")
            .WithMessage(CallerNumber.Message);
    }
}
