namespace Sankore.Modules.Kyc.Features.Verification.ManualValidation;

using FluentValidation;

internal sealed class ManuallyValidateKycFileValidator
    : AbstractValidator<ManuallyValidateKycFileCommand>
{
    /// <summary>Matches the <c>manual_validation_reason</c> column.</summary>
    internal const int MaximumReasonLength = 2000;

    /// <summary>
    /// Longer than the 10 characters a document refusal asks for. Overriding the machine on a whole
    /// file is the heavier act of the two, and it is read later by whoever has to sign the ladder —
    /// "ok" is not a ground.
    /// </summary>
    internal const int MinimumReasonLength = 20;

    public ManuallyValidateKycFileValidator()
    {
        RuleFor(x => x.KycFileId).NotEmpty();

        RuleFor(x => x.Reason)
            .NotEmpty()
            .MinimumLength(MinimumReasonLength)
            .MaximumLength(MaximumReasonLength)
            .WithMessage(
                "Un motif explicite est requis pour valider un dossier sans score biométrique "
                + "(service indisponible, pièce usée refusée à répétition…). Il est conservé dans "
                + "la piste d'audit : n'y reportez aucune donnée du client.");
    }
}
