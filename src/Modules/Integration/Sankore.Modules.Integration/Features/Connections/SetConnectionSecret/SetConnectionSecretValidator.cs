namespace Sankore.Modules.Integration.Features.Connections.SetConnectionSecret;

using FluentValidation;

/// <summary>
/// Shape only. Whether the far end accepts the credential is the far end's answer, not ours:
/// validating a format here would reject a perfectly good key the day a CBS changes how it mints
/// them, and the symptom would be a 400 from us about someone else's contract.
///
/// <para>
/// A blank value is refused rather than treated as "clear it". Clearing a credential by sending
/// an empty string is how a screen wipes one by accident; deletion deserves its own explicit
/// route.
/// </para>
/// </summary>
internal sealed class SetConnectionSecretValidator : AbstractValidator<SetConnectionSecretCommand>
{
    public SetConnectionSecretValidator()
    {
        RuleFor(x => x.ConnectionId).NotEmpty().OverridePropertyName("connectionId");

        // The name decides WHICH slot is overwritten, so an unknown one is refused here rather
        // than silently creating a fourth vault entry nothing will ever read.
        RuleFor(x => x.Name)
            .Must(ConnectionSecretNames.IsKnown)
            .OverridePropertyName("name")
            .WithMessage($"name must be one of: {string.Join(", ", ConnectionSecretNames.All)}.");

        RuleFor(x => x.Value)
            .NotEmpty()
            .OverridePropertyName("value")
            .WithMessage("A credential value is required.")
            .MaximumLength(8192)
            .WithMessage("A credential value cannot exceed 8192 characters.");

        RuleFor(x => x.ExpiresAt)
            .Must(at => at > DateTimeOffset.UnixEpoch)
            .When(x => x.ExpiresAt is not null)
            .OverridePropertyName("expiresAt");
    }
}
