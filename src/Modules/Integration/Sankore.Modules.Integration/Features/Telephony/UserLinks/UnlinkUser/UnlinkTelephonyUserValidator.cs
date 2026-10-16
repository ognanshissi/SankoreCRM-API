namespace Sankore.Modules.Integration.Features.Telephony.UserLinks.UnlinkUser;

using FluentValidation;

internal sealed class UnlinkTelephonyUserValidator : AbstractValidator<UnlinkTelephonyUserCommand>
{
    public UnlinkTelephonyUserValidator()
    {
        RuleFor(x => x.LinkId).NotEmpty().OverridePropertyName("linkId");

        // Validated here rather than only in the aggregate so the caller gets a 422 naming the
        // field. The aggregate refuses a blank reason as well, and must: the unlink is also
        // reachable from the daily check's repair path.
        RuleFor(x => x.Reason)
            .NotEmpty()
            .MaximumLength(500)
            .OverridePropertyName("reason")
            .WithMessage("reason is required and cannot exceed 500 characters.");
    }
}
