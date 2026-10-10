namespace Sankore.Shared.Infrastructure.Tests.Person;

using FluentAssertions;
using Sankore.Shared.Kernel;
using Sankore.Shared.Kernel.ValueObject;
using Xunit;

/// <summary>Covers <see cref="PepStatus"/> (BNF-B-02, BNF-B-05).</summary>
public sealed class PepStatusTests
{
    private static readonly DateOnly Today = new(2026, 10, 10);

    [Fact]
    public void NotExposed_carries_no_detail()
    {
        var s = PepStatus.NotExposed;

        s.IsPep.Should().BeFalse();
        s.Function.Should().BeNull();
        s.Link.Should().BeNull();
        s.CountryCode.Should().BeNull();
        s.FunctionEndDate.Should().BeNull();
    }

    [Fact]
    public void Exposed_requires_a_function()
    {
        // An exposure with no function says only that somebody ticked a box, and the vigilance
        // level it drives would not be justifiable to a regulator.
        var act = () => PepStatus.Exposed("   ", PepLink.Self);
        act.Should().Throw<DomainException>();
    }

    [Fact]
    public void Exposed_normalizes_the_country()
    {
        PepStatus.Exposed("Ministre", PepLink.Self, countryCode: "ci")
            .CountryCode.Should().Be("CI");
    }

    [Fact]
    public void Exposed_refuses_a_malformed_country()
    {
        var act = () => PepStatus.Exposed("Ministre", PepLink.Self, countryCode: "CIV");
        act.Should().Throw<DomainException>();
    }

    // ── The post-mandate window: the single definition (BNF-B-05) ──────────

    [Fact]
    public void A_person_in_office_is_exposed_whatever_the_window()
    {
        var inOffice = PepStatus.Exposed("Ministre", PepLink.Self);

        inOffice.IsExposedAt(Today, postMandateMonths: 0).Should().BeTrue();
        inOffice.IsExposedAt(Today, postMandateMonths: 12).Should().BeTrue();
    }

    [Theory]
    // mandate ended 2026-01-10; a 12-month window runs to 2027-01-10
    [InlineData("2026-10-10", 12, true)]   // nine months later — still exposed
    [InlineData("2027-01-10", 12, true)]   // the last day of the window, inclusive
    [InlineData("2027-01-11", 12, false)]  // the day after
    [InlineData("2026-10-10", 0, false)]   // window closed at the mandate's end
    [InlineData("2026-01-10", 0, true)]    // the day it ended, with no window
    public void Exposure_outlives_the_mandate_by_the_configured_window(
        string asOf, int months, bool expected)
    {
        var former = PepStatus.Exposed(
            "Ministre", PepLink.Self, functionEndDate: new DateOnly(2026, 1, 10));

        former
            .IsExposedAt(DateOnly.Parse(asOf, System.Globalization.CultureInfo.InvariantCulture), months)
            .Should().Be(expected);
    }

    [Fact]
    public void A_negative_window_narrows_rather_than_inverts()
    {
        // A misconfigured parameter must close the window, never run it backwards into
        // "exposed before the mandate started".
        var former = PepStatus.Exposed(
            "Ministre", PepLink.Self, functionEndDate: new DateOnly(2026, 1, 10));

        former.IsExposedAt(new DateOnly(2026, 1, 10), -36).Should().BeTrue("the end date itself");
        former.IsExposedAt(new DateOnly(2026, 1, 11), -36).Should().BeFalse();
    }

    [Fact]
    public void A_person_who_is_not_pep_is_never_exposed()
    {
        PepStatus.NotExposed.IsExposedAt(Today, 120).Should().BeFalse();
    }

    // ── Household propagation (BNF-B-05) ──────────────────────────────────

    [Fact]
    public void AsFamilyMemberLink_relinks_but_keeps_the_function_and_country()
    {
        var principal = PepStatus.Exposed(
            "Ministre des Finances", PepLink.Self, countryCode: "CI",
            functionEndDate: new DateOnly(2026, 1, 10));

        var spouse = principal.AsFamilyMemberLink();

        spouse.IsPep.Should().BeTrue();
        spouse.Link.Should().Be(PepLink.FamilyMember);
        spouse.Function.Should().Be("Ministre des Finances");
        spouse.CountryCode.Should().Be("CI");
        spouse.FunctionEndDate.Should().Be(new DateOnly(2026, 1, 10),
            "the household's exposure ends with the principal's mandate, on the same clock");
    }

    [Fact]
    public void AsFamilyMemberLink_drops_the_principals_override_reason()
    {
        // The reason justifies the PRINCIPAL's classification. Carrying it onto the household
        // would attribute a decision to a person nobody made it about.
        var principal = PepStatus.Exposed(
            "Ministre", PepLink.CloseAssociate, overrideReason: "declared by the branch manager");

        principal.AsFamilyMemberLink().OverrideReason.Should().BeNull();
    }

    [Fact]
    public void AsFamilyMemberLink_on_a_clean_principal_is_a_no_op()
    {
        // Callers apply it unconditionally over a whole household; it must not invent an exposure.
        PepStatus.NotExposed.AsFamilyMemberLink().IsPep.Should().BeFalse();
    }
}
