namespace Sankore.Modules.Integration.Features.Telephony.UserLinks.SetCallerNumber;

using FluentValidation;
using Sankore.Modules.Integration.Features.Telephony;

internal sealed class SetTelephonyCallerNumberValidator
    : AbstractValidator<SetTelephonyCallerNumberCommand>
{
    public SetTelephonyCallerNumberValidator()
    {
        RuleFor(x => x.LinkId).NotEmpty().OverridePropertyName("linkId");

        // No NotEmpty: a blank value is how the caller clears the override.
        RuleFor(x => x.DefaultCallerNumber)
            .Must(CallerNumber.IsWellFormed)
            .When(x => !string.IsNullOrWhiteSpace(x.DefaultCallerNumber))
            .OverridePropertyName("defaultCallerNumber")
            .WithMessage(CallerNumber.Message);
    }
}
