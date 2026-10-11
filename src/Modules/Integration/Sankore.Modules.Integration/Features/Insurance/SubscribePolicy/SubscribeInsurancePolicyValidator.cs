namespace Sankore.Modules.Integration.Features.Insurance.SubscribePolicy;

using FluentValidation;

/// <summary>
/// Request-shaped rules. The invariants that condition the existence of a row — a positive
/// premium, a consent proof that actually holds evidence — stay in the aggregates and throw; they
/// are repeated here so a caller gets a 400 naming the field instead of a 500.
/// </summary>
internal sealed class SubscribeInsurancePolicyValidator
    : AbstractValidator<SubscribeInsurancePolicyCommand>
{
    public SubscribeInsurancePolicyValidator()
    {
        RuleFor(x => x.ProductId).NotEmpty();
        RuleFor(x => x.CrmCustomerId).NotEmpty();

        // The account to debit. Required here and not merely by the CBS: a subscription with no
        // account is one whose first step could never be queued, and the caller should learn that
        // before a saga row exists.
        RuleFor(x => x.AccountRef).NotEmpty().MaximumLength(64);

        RuleFor(x => x.PremiumAmount)
            .GreaterThan(0m)
            .When(x => x.PremiumAmount.HasValue)
            .WithMessage("A premium must be a positive amount.");

        // ISO 4217, three letters. Optional because the catalogue's own currency is the fallback.
        RuleFor(x => x.Currency)
            .Length(3)
            .When(x => !string.IsNullOrWhiteSpace(x.Currency));

        // ── The consent (CIMA 2024) ─────────────────────────────────────────

        RuleFor(x => x.ConsentTextVersion)
            .NotEmpty()
            .MaximumLength(40)
            .WithMessage(
                "A consent proof must name the version of the notice the customer accepted; "
                + "without it the proof does not say what was consented to.");

        // One or the other, never neither. The aggregate refuses it too — this is where the
        // caller is told WHICH field is missing, because a proof claiming consent with nothing to
        // show a controller is the one shape the 2024 regulation makes unusable.
        RuleFor(x => x)
            .Must(x => !string.IsNullOrWhiteSpace(x.ConsentEvidence)
                    || !string.IsNullOrWhiteSpace(x.ConsentEvidenceStorageRef))
            .WithMessage(
                "A consent proof needs its evidence: either inline (consentEvidence) or as a "
                + "reference to the stored object (consentEvidenceStorageRef).");

        // A consent dated in the future has not been given.
        RuleFor(x => x.ConsentedAt)
            .LessThanOrEqualTo(_ => DateTimeOffset.UtcNow.AddMinutes(5))
            .WithMessage(
                "A consent cannot be dated in the future. Five minutes of clock skew is "
                + "tolerated, because the timestamp often comes from an agent's own device.");

        // ── The medical questionnaire ───────────────────────────────────────
        //
        // Both halves or neither: a code with no answers is a row asserting a questionnaire was
        // collected while holding nothing, and answers with no code cannot be read back against
        // the questions that were asked.
        RuleFor(x => x)
            .Must(x => string.IsNullOrWhiteSpace(x.MedicalQuestionnaireCode)
                    == string.IsNullOrWhiteSpace(x.MedicalAnswers))
            .WithMessage(
                "A medical questionnaire needs both its code and its answers, or neither.");

        RuleFor(x => x.MedicalQuestionnaireCode).MaximumLength(40);
    }
}
