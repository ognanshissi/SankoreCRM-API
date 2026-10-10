namespace Sankore.Modules.Integration.Tests.Features.Insurance;

using FluentAssertions;
using Sankore.Modules.Integration.Domain;
using Sankore.Modules.Integration.Features.Insurance;
using Sankore.Modules.Integration.PublicApi;
using Xunit;

/// <summary>
/// ASS-03's eligibility rules — « âge, niveau KYC, détention d'un compte ou d'un crédit » — and
/// ASS-04's third criterion, « un refus est expliqué à l'agent ».
///
/// <para>
/// Pure: no database, no clock, no module double. That is the whole point of
/// <c>ApplicantFacts</c> being a record of facts rather than a set of module calls.
/// </para>
/// </summary>
public sealed class InsuranceEligibilityRulesTests
{
    private static InsuranceProduct Product(
        int? minAge = null,
        int? maxAge = null,
        KycLevel minKycLevel = KycLevel.None,
        bool requiresCbsAccount = false,
        bool requiresActiveLoan = false)
        => InsuranceProduct.Create(
            tenantId: Guid.NewGuid(),
            connectionId: Guid.NewGuid(),
            insurerProductCode: "ASS-VIE-EMP",
            name: "Assurance emprunteur",
            periodicity: PremiumPeriodicity.Annual,
            pricingMode: ProductPricingMode.CatalogueFixed,
            createdBy: Guid.NewGuid(),
            clock: TimeProvider.System,
            effectiveFrom: new DateOnly(2026, 1, 1),
            fixedPremiumAmount: 7_500m,
            currency: "XOF",
            minAge: minAge,
            maxAge: maxAge,
            minKycLevel: minKycLevel,
            requiresCbsAccount: requiresCbsAccount,
            requiresActiveLoan: requiresActiveLoan);

    private static ApplicantFacts Facts(
        int? age = 40,
        KycLevel? kyc = KycLevel.Full,
        bool? account = true,
        bool? loan = true)
        => new(age, kyc, account, loan);

    [Fact]
    public void A_product_with_no_rules_is_satisfied_by_an_applicant_we_know_nothing_about()
    {
        var verdict = InsuranceEligibilityRules.Evaluate(
            Product(), new ApplicantFacts(null, null, null, null));

        // The unknown facts must NOT refuse here: a product that constrains nothing cannot be
        // refused for a missing date of birth. If this ever fails, every unconstrained product
        // becomes unsellable to a customer whose file is thin.
        verdict.IsEligible.Should().BeTrue();
        verdict.Reasons.Should().BeEmpty();
    }

    [Theory]
    [InlineData(17, InsuranceEligibilityRules.TooYoung)]
    [InlineData(71, InsuranceEligibilityRules.TooOld)]
    public void An_age_outside_the_window_is_refused_by_name(int age, string expected)
    {
        var verdict = InsuranceEligibilityRules.Evaluate(
            Product(minAge: 18, maxAge: 70), Facts(age: age));

        verdict.IsEligible.Should().BeFalse();
        verdict.Reasons.Should().ContainSingle().Which.Should().Be(expected);
    }

    [Fact]
    public void An_age_the_platform_does_not_know_is_NOT_treated_as_eligible()
    {
        var verdict = InsuranceEligibilityRules.Evaluate(
            Product(minAge: 18, maxAge: 70), Facts(age: null));

        // The decision this test exists for. A customer whose date of birth M01 does not hold is
        // not "of eligible age" — approving a life policy on that basis is the failure the
        // nullability of ApplicantFacts exists to prevent, and the reason is distinct from a real
        // age refusal so the agent knows to go and fill the file in.
        verdict.IsEligible.Should().BeFalse();
        verdict.Reasons.Should().ContainSingle().Which.Should().Be(InsuranceEligibilityRules.AgeUnknown);
    }

    [Fact]
    public void A_lower_KYC_tier_than_the_product_demands_is_refused()
    {
        InsuranceEligibilityRules
            .Evaluate(Product(minKycLevel: KycLevel.Full), Facts(kyc: KycLevel.Simplified))
            .Reasons.Should().Contain(InsuranceEligibilityRules.KycInsufficient);

        InsuranceEligibilityRules
            .Evaluate(Product(minKycLevel: KycLevel.Simplified), Facts(kyc: KycLevel.Full))
            .IsEligible.Should().BeTrue();
    }

    /// <summary>
    /// The tier comparison is numeric, so the ENUM ORDER is the rule. Pinned here because a
    /// reordering of <see cref="KycLevel"/> would silently invert the comparison in
    /// <c>InsuranceEligibilityRules</c> and let a Simplified customer satisfy a Full requirement —
    /// with every other test still green.
    /// </summary>
    [Fact]
    public void The_KYC_tiers_are_ordered_None_then_Simplified_then_Full()
    {
        ((int)KycLevel.None).Should().BeLessThan((int)KycLevel.Simplified);
        ((int)KycLevel.Simplified).Should().BeLessThan((int)KycLevel.Full);
    }

    [Fact]
    public void A_missing_account_or_loan_is_refused_and_an_unknown_one_is_refused_differently()
    {
        InsuranceEligibilityRules
            .Evaluate(Product(requiresCbsAccount: true), Facts(account: false))
            .Reasons.Should().Contain(InsuranceEligibilityRules.NoCbsAccount);

        InsuranceEligibilityRules
            .Evaluate(Product(requiresCbsAccount: true), Facts(account: null))
            .Reasons.Should().Contain(InsuranceEligibilityRules.CbsAccountUnknown);

        InsuranceEligibilityRules
            .Evaluate(Product(requiresActiveLoan: true), Facts(loan: false))
            .Reasons.Should().Contain(InsuranceEligibilityRules.NoActiveLoan);

        InsuranceEligibilityRules
            .Evaluate(Product(requiresActiveLoan: true), Facts(loan: null))
            .Reasons.Should().Contain(InsuranceEligibilityRules.ActiveLoanUnknown);
    }

    [Fact]
    public void A_refusal_carries_EVERY_reason_and_not_the_first()
    {
        var verdict = InsuranceEligibilityRules.Evaluate(
            Product(
                minAge: 18, maxAge: 70, minKycLevel: KycLevel.Full,
                requiresCbsAccount: true, requiresActiveLoan: true),
            Facts(age: 15, kyc: KycLevel.Simplified, account: false, loan: false));

        // ASS-04's « un refus est expliqué à l'agent »: one reason at a time turns one counter
        // conversation into four, and the customer leaves each time without knowing what to fix.
        verdict.IsEligible.Should().BeFalse();
        verdict.Reasons.Should().BeEquivalentTo(
        [
            InsuranceEligibilityRules.TooYoung,
            InsuranceEligibilityRules.KycInsufficient,
            InsuranceEligibilityRules.NoCbsAccount,
            InsuranceEligibilityRules.NoActiveLoan,
        ]);
    }

    /// <summary>
    /// The verdict is the SAME record <c>IInsuranceProductPort.CheckEligibilityAsync</c> returns,
    /// so a tenant rule and an insurer rule are indistinguishable to the caller and their reasons
    /// concatenate into one explanation. A separate local type would have forced the subscription
    /// handler to merge two shapes.
    /// </summary>
    [Fact]
    public void The_verdict_is_the_contracts_own_eligibility_record()
    {
        InsuranceEligibilityRules.Evaluate(Product(), Facts())
            .Should().BeOfType<InsuranceEligibility>();
    }
}
