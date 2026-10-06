namespace Sankore.Modules.Kyc.Features.Documents.ReviewDocument;

using FluentValidation;
using Sankore.Modules.Kyc.Domain;

internal sealed class ReviewKycDocumentValidator : AbstractValidator<ReviewKycDocumentCommand>
{
    /// <summary>Matches the <c>refusal_reason</c> column — a longer motive would be truncated.</summary>
    internal const int MaximumReasonLength = 2000;

    /// <summary>Long enough to be a sentence an agent can act on, short enough not to be a case note.</summary>
    internal const int MinimumReasonLength = 10;

    public ReviewKycDocumentValidator()
    {
        RuleFor(x => x.KycFileId).NotEmpty();
        RuleFor(x => x.DocumentId).NotEmpty();

        // Pending and NotReviewed are states, not decisions.
        RuleFor(x => x.Decision)
            .IsInEnum()
            .Must(d => d is KycDocumentReviewDecision.Accepted or KycDocumentReviewDecision.Refused)
            .WithMessage("La décision doit être « Accepted » ou « Refused ».");

        RuleFor(x => x.Reason).MaximumLength(MaximumReasonLength);

        // Mandatory when the answer is no. The agent has to re-photograph something, and
        // "Refusé" with no motive tells them to guess which document and what was wrong with it.
        RuleFor(x => x.Reason)
            .NotEmpty()
            .MinimumLength(MinimumReasonLength)
            .WithMessage(
                "Un motif explicite est requis pour refuser un document. Il est lu par l'agent et "
                + "conservé dans la piste d'audit : n'y reportez aucune donnée du client "
                + "(numéro de pièce, téléphone, adresse).")
            .When(x => x.Decision == KycDocumentReviewDecision.Refused);
    }
}
