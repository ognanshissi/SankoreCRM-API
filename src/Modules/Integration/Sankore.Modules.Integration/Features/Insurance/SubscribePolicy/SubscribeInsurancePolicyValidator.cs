namespace Sankore.Modules.Integration.Features.Insurance.SubscribePolicy;

using FluentValidation;
using Sankore.Modules.Integration.Domain;

/// <summary>
/// The request's shape. Whether the holding, the product and the client allow the subscription is
/// the handler's: those need data.
/// </summary>
internal sealed class SubscribeInsurancePolicyValidator
    : AbstractValidator<SubscribeInsurancePolicyCommand>
{
    public SubscribeInsurancePolicyValidator()
    {
        RuleFor(x => x.HoldingId).NotEmpty();
        RuleFor(x => x.ProductId).NotEmpty();

        // ── The premium's payment: one half or the other, by mode ───────────
        RuleFor(x => x.AccountRef)
            .NotEmpty()
            .MaximumLength(64)
            .When(x => x.PaymentMode == PremiumPaymentMode.CoreBankingDebit)
            .WithMessage("A premium debited from core banking needs the account to debit.");

        When(x => x.PaymentMode == PremiumPaymentMode.RecordedByAgent, () =>
        {
            RuleFor(x => x.PaymentMethod)
                .NotNull()
                .WithMessage("A premium recorded by the agent needs how it was paid.");

            RuleFor(x => x.PaymentReference)
                .NotEmpty()
                .MaximumLength(200)
                .WithMessage(
                    "A premium recorded by the agent needs its receipt, transfer or Mobile Money "
                    + "reference: it is the only proof the money was taken.");

            RuleFor(x => x.PaidOn)
                .NotNull()
                .WithMessage("A premium recorded by the agent needs the day it was paid.");
        });

        When(x => x.PaymentMode == PremiumPaymentMode.DeferredByDerogation, () =>
        {
            RuleFor(x => x.CommitmentReference)
                .NotEmpty()
                .MaximumLength(200)
                .WithMessage("The 60-day derogation needs the reference of the subscriber's written commitment.");

            RuleFor(x => x.CommitmentSignedOn)
                .NotNull()
                .WithMessage("The 60-day derogation needs the day the commitment was signed.");
        });

        RuleFor(x => x.PremiumAmount)
            .GreaterThan(0m)
            .When(x => x.PremiumAmount.HasValue)
            .WithMessage("A premium must be a positive amount.");

        RuleFor(x => x.Currency)
            .Length(3)
            .When(x => !string.IsNullOrWhiteSpace(x.Currency));

        // ── Consent ──────────────────────────────────────────────────────────
        //
        // The version of the notice is what makes the proof say what was consented to; the
        // evidence is what a controller is shown.
        RuleFor(x => x.ConsentTextVersion)
            .NotEmpty()
            .MaximumLength(40)
            .WithMessage(
                "A consent proof must name the version of the notice the customer accepted; "
                + "without it the proof does not say what was consented to.");

        RuleFor(x => x)
            .Must(x => !string.IsNullOrWhiteSpace(x.ConsentEvidence)
                    || !string.IsNullOrWhiteSpace(x.ConsentEvidenceStorageRef))
            .WithMessage(
                "A consent proof needs its evidence: either inline (consentEvidence) or as a "
                + "reference to the stored object (consentEvidenceStorageRef).");

        // Five minutes of clock skew: the timestamp often comes from an agent's own device.
        RuleFor(x => x.ConsentedAt)
            .LessThanOrEqualTo(_ => DateTimeOffset.UtcNow.AddMinutes(5))
            .WithMessage("A consent cannot be dated in the future.");
    }
}
