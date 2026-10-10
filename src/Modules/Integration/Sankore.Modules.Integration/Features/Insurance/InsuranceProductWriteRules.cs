namespace Sankore.Modules.Integration.Features.Insurance;

using FluentValidation;
using Sankore.Modules.Integration.Domain;

/// <summary>
/// The rules the create and the update of a product share.
///
/// <para>
/// One rule set, included by both validators, so the two cannot drift — the drift that leaves a
/// field refused on creation and accepted on edit, which is how an invalid row gets in through
/// the back door. Lengths mirror the column widths of <c>ins_product</c>: refusing here names the
/// field in a 422, where letting it through gives a truncation or a database error.
/// </para>
///
/// <para>
/// <c>.OverridePropertyName</c> and never <c>.WithName</c>: the former sets the KEY of the 422
/// payload, which is what a form binds its field errors to.
/// </para>
/// </summary>
internal sealed class InsuranceProductBodyValidator : AbstractValidator<InsuranceProductWriteRequest>
{
    public InsuranceProductBodyValidator()
    {
        RuleFor(b => b.Name)
            .NotEmpty().WithMessage("A product name is required.")
            .MaximumLength(200)
            .OverridePropertyName("name");

        RuleFor(b => b.Description)
            .MaximumLength(2000)
            .OverridePropertyName("description");

        // A catalogue-priced product with no price would be offered at the counter with nothing
        // to debit — the aggregate refuses it too, and this is the half that names the field.
        When(b => b.PricingMode == ProductPricingMode.CatalogueFixed, () =>
        {
            RuleFor(b => b.FixedPremiumAmount)
                .NotNull().WithMessage(
                    "A catalogue-priced product needs its premium: it is the amount that will be "
                    + "debited from the customer's account.")
                .GreaterThan(0m).WithMessage("A premium must be positive.")
                .OverridePropertyName("fixedPremiumAmount");

            RuleFor(b => b.Currency)
                .NotEmpty().WithMessage("A premium amount requires its currency.")
                .Length(3).WithMessage("A currency is an ISO 4217 code of three letters.")
                .OverridePropertyName("currency");
        });

        RuleFor(b => b.InsuredAmount)
            .GreaterThan(0m).When(b => b.InsuredAmount is not null)
            .WithMessage("An insured amount must be positive.")
            .OverridePropertyName("insuredAmount");

        // A plausible human lifetime rather than int.MaxValue: an age typed into the wrong field
        // is the ordinary mistake, and 0–130 catches it while refusing nobody real.
        RuleFor(b => b.MinAge)
            .InclusiveBetween(0, 130).When(b => b.MinAge is not null)
            .OverridePropertyName("minAge");

        RuleFor(b => b.MaxAge)
            .InclusiveBetween(0, 130).When(b => b.MaxAge is not null)
            .OverridePropertyName("maxAge");

        RuleFor(b => b.MaxAge)
            .GreaterThanOrEqualTo(b => b.MinAge!.Value)
            .When(b => b.MinAge is not null && b.MaxAge is not null)
            .WithMessage("The maximum age cannot be below the minimum age.")
            .OverridePropertyName("maxAge");

        RuleFor(b => b.CommissionRate)
            .InclusiveBetween(0m, 1m)
            .WithMessage("A commission rate is a fraction between 0 and 1 — 0.15 for 15%.")
            .OverridePropertyName("commissionRate");

        // 0 is legitimate: a product whose unpaid premiums go straight to the insurer with no
        // retry at all. 10 is the ceiling because a daily retry for a fortnight on a refused
        // account is harassment, not collection.
        RuleFor(b => b.PremiumRetryLimit)
            .InclusiveBetween(0, 10)
            .OverridePropertyName("premiumRetryLimit");

        RuleFor(b => b.PremiumRetryIntervalDays)
            .InclusiveBetween(1, 90)
            .WithMessage(
                "A retry interval of less than a day would re-debit a refused account the same "
                + "day.")
            .OverridePropertyName("premiumRetryIntervalDays");

        RuleFor(b => b.EffectiveTo)
            .GreaterThanOrEqualTo(b => b.EffectiveFrom)
            .When(b => b.EffectiveTo is not null)
            .WithMessage("The validity window ends before it starts.")
            .OverridePropertyName("effectiveTo");

        RuleFor(b => b.LinkedCreditProductCode)
            .MaximumLength(100)
            .OverridePropertyName("linkedCreditProductCode");

        // 50, matching ProductSpeciality.Code in M12 — not 100 like the insurer's code. A longer
        // value could never resolve to a catalogue entry, so refusing it here names the field
        // instead of letting the write-time lookup report it as "no such product".
        RuleFor(b => b.CrmProductCode)
            .MaximumLength(50)
            .OverridePropertyName("crmProductCode");

        RuleForEach(b => b.Guarantees)
            .ChildRules(g =>
            {
                g.RuleFor(x => x.Code).NotEmpty().MaximumLength(60);
                g.RuleFor(x => x.Label).NotEmpty().MaximumLength(200);
            })
            .When(b => b.Guarantees is not null)
            .OverridePropertyName("guarantees");

        // The one bound the column cannot carry: `jsonb` takes no length cap, so an unbounded
        // guarantee list would be accepted by the database and make every catalogue read heavy.
        RuleFor(b => b.Guarantees)
            .Must(g => InsuranceGuaranteeCodec.Serialise(g).Length
                       <= InsuranceProduct.MaxGuaranteesJsonLength)
            .When(b => b.Guarantees is not null)
            .WithMessage(
                $"The guarantee list is too large (limit {InsuranceProduct.MaxGuaranteesJsonLength} "
                + "characters serialised).")
            .OverridePropertyName("guarantees");
    }
}
