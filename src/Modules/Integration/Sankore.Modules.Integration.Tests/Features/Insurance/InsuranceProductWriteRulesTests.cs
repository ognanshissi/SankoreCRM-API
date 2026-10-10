namespace Sankore.Modules.Integration.Tests.Features.Insurance;

using FluentAssertions;
using Sankore.Modules.Integration.Domain;
using Sankore.Modules.Integration.Features.Insurance;
using Sankore.Modules.Integration.Features.Insurance.CreateInsuranceProduct;
using Sankore.Modules.Integration.Features.Insurance.UpdateInsuranceProduct;
using Sankore.Modules.Integration.PublicApi;
using Xunit;

/// <summary>
/// The write rules of a catalogue entry, and the 422 keys they produce.
/// </summary>
public sealed class InsuranceProductWriteRulesTests
{
    private static readonly InsuranceProductBodyValidator Body = new();

    [Fact]
    public void A_complete_body_passes()
    {
        Body.Validate(InsuranceTestHarness.Body()).IsValid.Should().BeTrue();
    }

    [Fact]
    public void A_catalogue_priced_product_without_a_premium_is_refused_on_the_premium_field()
    {
        var result = Body.Validate(InsuranceTestHarness.Body(fixedPremium: null));

        result.IsValid.Should().BeFalse();

        // The KEY matters, not just the refusal: a form binds its field errors to it. Produced by
        // .OverridePropertyName — .WithName would set the message's display name and leave the key
        // as the expression path.
        result.Errors.Should().Contain(e => e.PropertyName == "fixedPremiumAmount");
    }

    [Fact]
    public void A_catalogue_priced_product_without_a_currency_is_refused_on_the_currency_field()
    {
        Body.Validate(InsuranceTestHarness.Body(currency: null))
            .Errors.Should().Contain(e => e.PropertyName == "currency");

        // Three letters, ISO 4217. "XOFF" is the plausible typo and the one that would reach a
        // debit instruction.
        Body.Validate(InsuranceTestHarness.Body(currency: "XOFF"))
            .Errors.Should().Contain(e => e.PropertyName == "currency");
    }

    [Fact]
    public void An_insurer_priced_product_needs_no_premium_in_the_catalogue()
    {
        // The complement of the two above: ASS-03's second branch is a complete configuration, not
        // an unfinished one, so the premium rules must NOT fire for it.
        Body.Validate(InsuranceTestHarness.Body(
                pricingMode: ProductPricingMode.InsurerComputed,
                fixedPremium: null,
                currency: null))
            .IsValid.Should().BeTrue();
    }

    [Fact]
    public void An_inverted_age_window_is_refused()
    {
        Body.Validate(InsuranceTestHarness.Body(minAge: 70, maxAge: 18))
            .Errors.Should().Contain(e => e.PropertyName == "maxAge");
    }

    [Theory]
    [InlineData(-0.01)]
    [InlineData(1.01)]
    [InlineData(15)]
    public void A_commission_rate_outside_zero_to_one_is_refused(decimal rate)
    {
        // 15 is the mistake this rule exists for: an administrator typing a PERCENTAGE into a
        // field that holds a fraction. Unchecked, every statement line of ASS-10 would claim
        // fifteen times the premium as commission.
        Body.Validate(InsuranceTestHarness.Body(commissionRate: rate))
            .Errors.Should().Contain(e => e.PropertyName == "commissionRate");
    }

    [Fact]
    public void A_retry_interval_below_one_day_is_refused_and_zero_retries_is_allowed()
    {
        Body.Validate(InsuranceTestHarness.Body(premiumRetryIntervalDays: 0))
            .Errors.Should().Contain(e => e.PropertyName == "premiumRetryIntervalDays");

        // Zero RETRIES is a legitimate policy — the impayé goes straight to the insurer — and must
        // not be confused with a zero interval, which would re-debit a refused account the same
        // day.
        Body.Validate(InsuranceTestHarness.Body(premiumRetryLimit: 0)).IsValid.Should().BeTrue();
    }

    [Fact]
    public void A_validity_window_that_ends_before_it_starts_is_refused()
    {
        Body.Validate(InsuranceTestHarness.Body(
                effectiveFrom: new DateOnly(2026, 6, 1),
                effectiveTo: new DateOnly(2026, 1, 1)))
            .Errors.Should().Contain(e => e.PropertyName == "effectiveTo");
    }

    [Fact]
    public void A_guarantee_without_a_code_or_a_label_is_refused()
    {
        Body.Validate(InsuranceTestHarness.Body(
                guarantees: [new InsuranceGuarantee(string.Empty, "Décès", null, null)]))
            .IsValid.Should().BeFalse();

        Body.Validate(InsuranceTestHarness.Body(
                guarantees: [new InsuranceGuarantee("DC", string.Empty, null, null)]))
            .IsValid.Should().BeFalse();
    }

    [Fact]
    public void An_oversized_guarantee_list_is_refused_because_the_jsonb_column_cannot_bound_it()
    {
        var many = Enumerable.Range(0, 400)
            .Select(i => new InsuranceGuarantee($"G{i:D3}", new string('x', 200), 1_000_000m, 5_000m))
            .ToList();

        var result = Body.Validate(InsuranceTestHarness.Body(guarantees: many));

        // PostgreSQL offers no length cap on jsonb, so this is the only place the bound can live.
        // Without it an unbounded list is accepted by the database and every catalogue read
        // carries it.
        result.Errors.Should().Contain(e => e.PropertyName == "guarantees");
    }

    [Fact]
    public void The_create_command_requires_the_insurers_own_product_code()
    {
        var validator = new CreateInsuranceProductValidator();

        var result = validator.Validate(new CreateInsuranceProductCommand(
            Guid.NewGuid(), "   ", InsuranceTestHarness.Body()));

        result.Errors.Should().Contain(e => e.PropertyName == "insurerProductCode");
    }

    [Fact]
    public void Create_and_update_refuse_the_SAME_bodies()
    {
        // The drift this guards: a field refused on creation and accepted on edit is how an
        // invalid row gets in through the back door. Both validators compose the one rule set, and
        // this asserts the composition actually happened on both.
        var invalid = InsuranceTestHarness.Body(fixedPremium: null, commissionRate: 15m);

        var created = new CreateInsuranceProductValidator().Validate(
            new CreateInsuranceProductCommand(Guid.NewGuid(), "ASS-VIE-EMP", invalid));

        var updated = new UpdateInsuranceProductValidator().Validate(
            new UpdateInsuranceProductCommand(Guid.NewGuid(), invalid));

        created.IsValid.Should().BeFalse();
        updated.IsValid.Should().BeFalse();

        created.Errors.Select(e => e.PropertyName).Should()
            .BeEquivalentTo(updated.Errors.Select(e => e.PropertyName));
    }
}
