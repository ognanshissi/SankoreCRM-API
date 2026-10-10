namespace Sankore.Modules.Integration.Features.RelayAgents.ExchangeEnrolmentToken;

using FluentValidation;

/// <summary>
/// Shape only, and deliberately blunt about the token.
///
/// <para>
/// <b>The token's format is NOT validated beyond being present and bounded.</b> A rule that
/// refused anything but 64 hexadecimal characters would answer 422 for a malformed value and 401
/// for a well-formed unknown one — a free classifier that tells a prober which of its guesses
/// even have the right shape. The length ceiling stays because an unbounded body on an anonymous
/// endpoint is work we perform before knowing whether to care, and 512 is already an order of
/// magnitude more than a token needs.
/// </para>
///
/// <para>
/// The thumbprint rule guards an INVARIANT rather than user input: the value is computed by the
/// endpoint from the TLS handshake and cannot reach this command from a body at all, so a failure
/// here would mean our own code produced something that is not a SHA-256 hex digest. It is kept
/// because the column is 64 characters wide and a value of any other shape could never match what
/// the admission check later computes — which would read as "the agent cannot connect", with
/// nothing to point at. A SHA-1 thumbprint (40 characters) is the realistic way that happens.
/// </para>
/// </summary>
internal sealed class ExchangeEnrolmentTokenValidator
    : AbstractValidator<ExchangeEnrolmentTokenCommand>
{
    public ExchangeEnrolmentTokenValidator()
    {
        RuleFor(x => x.Token)
            .NotEmpty()
            .OverridePropertyName("token")
            .WithMessage("An enrolment token is required.")
            .MaximumLength(512)
            .OverridePropertyName("token")
            .WithMessage("An enrolment token cannot exceed 512 characters.");

        RuleFor(x => x.CertificateThumbprint)
            .Must(RelayCertificateThumbprint.IsWellFormed)
            .OverridePropertyName("certificateThumbprint")
            .WithMessage(RelayCertificateThumbprint.Requirement);
    }
}
