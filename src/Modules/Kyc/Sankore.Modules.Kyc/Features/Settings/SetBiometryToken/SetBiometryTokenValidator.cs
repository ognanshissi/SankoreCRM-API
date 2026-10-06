namespace Sankore.Modules.Kyc.Features.Settings.SetBiometryToken;

using FluentValidation;

/// <summary>
/// Shape only. Whether the service accepts the token is the service's answer, not ours: validating
/// a format here would reject a perfectly good token the day the Flask side changes how it mints
/// them, and the symptom would be a 400 from us about someone else's contract.
///
/// <para>
/// A blank value is refused rather than treated as "clear it". Clearing a credential by sending an
/// empty string is how a screen wipes one by accident; deletion deserves its own explicit route.
/// </para>
/// </summary>
internal sealed class SetBiometryTokenValidator : AbstractValidator<SetBiometryTokenCommand>
{
    public SetBiometryTokenValidator()
    {
        RuleFor(x => x.Token)
            .NotEmpty()
            .WithMessage("A biometry service token is required.")
            .MaximumLength(512)
            .WithMessage("A biometry service token cannot exceed 512 characters.");
    }
}
