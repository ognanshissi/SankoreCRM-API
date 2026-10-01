namespace Sankore.Modules.Kyc.Features.Reviews.RaiseReview;

using FluentValidation;

internal sealed class RaiseKycReviewValidator : AbstractValidator<RaiseKycReviewCommand>
{
    /// <summary>Long enough to be a sentence, short enough not to become a case note.</summary>
    internal const int MinimumReasonLength = 10;
    internal const int MaximumReasonLength = 500;

    public RaiseKycReviewValidator()
    {
        RuleFor(x => x.KycFileId).NotEmpty();

        // A length floor, not a pattern: "alerte transaction" is a motive, "ok" is a click. The
        // field holds a justification and never a value — the French label must say so, because a
        // reason is stored in clear and read by whoever picks the review up.
        RuleFor(x => x.Reason)
            .NotEmpty()
            .MinimumLength(MinimumReasonLength)
            .MaximumLength(MaximumReasonLength)
            .WithMessage(
                "Un motif explicite est requis pour déclencher une revue KYC. "
                + "N'y portez aucune donnée sensible (numéro de pièce, téléphone, adresse).");

        // The handler re-checks the reason on its own, so the rule still holds for a caller that
        // reaches it without the validation pipeline.
        RuleFor(x => x.RaisedBy).NotEmpty();
    }
}
