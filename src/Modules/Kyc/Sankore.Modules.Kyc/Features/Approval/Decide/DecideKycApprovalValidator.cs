namespace Sankore.Modules.Kyc.Features.Approval.Decide;

using FluentValidation;
using Sankore.Modules.Kyc.Domain;

internal sealed class DecideKycApprovalValidator : AbstractValidator<DecideKycApprovalCommand>
{
    /// <summary>Matches the <c>comment</c> column — a longer motive would be silently truncated.</summary>
    internal const int MaximumCommentLength = 2000;

    /// <summary>Long enough to be a sentence an agent can act on, short enough not to be a case note.</summary>
    internal const int MinimumMotiveLength = 10;

    public DecideKycApprovalValidator()
    {
        RuleFor(x => x.KycFileId).NotEmpty();

        RuleFor(x => x.Level).IsInEnum();

        // Pending is a state, not a decision. Sending it would ask the aggregate to un-decide a
        // step, which is the one thing the circuit exists to make impossible.
        RuleFor(x => x.Decision)
            .IsInEnum()
            .NotEqual(KycApprovalDecision.Pending);

        RuleFor(x => x.Comment).MaximumLength(MaximumCommentLength);

        // Mandatory when the answer is no. A refusal travels to M01 on KycRejectedEvent and a
        // complement request goes back to the agent as a to-do list: both are read by somebody who
        // has to do something about it, and "Rejeté" with no motive is a ticket, not a decision.
        // An approval needs none — nothing has to be explained to anybody.
        RuleFor(x => x.Comment)
            .NotEmpty()
            .MinimumLength(MinimumMotiveLength)
            .WithMessage("Un motif explicite est requis pour refuser un dossier ou demander un complément.")
            .When(x => x.Decision is KycApprovalDecision.Rejected
                                  or KycApprovalDecision.ComplementRequired);
    }
}
