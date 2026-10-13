namespace Sankore.Modules.Kyc.Features.RemoteInvitations.RevokeInvitation;

using FluentValidation;

internal sealed class RevokeKycRemoteInvitationValidator
    : AbstractValidator<RevokeKycRemoteInvitationCommand>
{
    /// <summary>
    /// Long enough to be a motive rather than a keystroke. "ok" and "." are what a mandatory free
    /// text field collects when nothing stops them, and they are worse than no field at all — they
    /// make the audit trail look complete.
    /// </summary>
    private const int MinimumReasonLength = 5;

    public RevokeKycRemoteInvitationValidator()
    {
        RuleFor(c => c.KycFileId).NotEmpty();
        RuleFor(c => c.InvitationId).NotEmpty();

        RuleFor(c => c.Reason)
            .NotEmpty()
            .MinimumLength(MinimumReasonLength)
            // Same ceiling as the column and as every other operator-motive field of this module.
            .MaximumLength(2000);
    }
}
