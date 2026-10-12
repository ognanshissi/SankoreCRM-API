namespace Sankore.Modules.Administration.Tests.Domain;

using FluentAssertions;
using Sankore.Modules.Administration.Domain;
using Xunit;

/// <summary>Covers <see cref="ComplianceSettings"/> (BNF-B-10, BNF-B-05).</summary>
public sealed class ComplianceSettingsTests
{
    private static readonly Guid Tenant = Guid.NewGuid();
    private static readonly Guid Actor = Guid.NewGuid();
    private static readonly DateTimeOffset Now = new(2026, 10, 10, 12, 0, 0, TimeSpan.Zero);

    private const string ValidGiin = "98Q96B.00000.LE.384";

    private static ComplianceSettings Fresh() => ComplianceSettings.CreateDefault(Tenant, Actor, Now);

    // ── Defaults ──────────────────────────────────────────────────────────

    [Fact]
    public void A_new_tenant_starts_with_fatca_off()
    {
        var s = Fresh();

        s.FatcaEnabled.Should().BeFalse();
        s.FatcaRegime.Should().BeNull();
        s.FatcaGiin.Should().BeNull();
        s.FatcaRequiredAt.Should().Be(FatcaRequiredAt.Conversion);
        s.FatcaForCoHolders.Should().BeTrue(
            "a joint account's co-holder is as reportable as its holder; defaulting this false "
            + "would silently narrow the obligation");
        s.PepPostMandateMonths.Should().Be(12);
    }

    // ── GIIN shape ────────────────────────────────────────────────────────

    [Theory]
    [InlineData("98Q96B.00000.LE.384")]
    [InlineData("A1B2C3.44444.SL.250")]
    [InlineData("98q96b.00000.le.384")]   // lower case is accepted and upper-cased on write
    public void Accepts_a_well_formed_giin(string giin)
    {
        ComplianceSettings.GiinIsWellFormed(giin).Should().BeTrue();
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("98Q96B.00000.LE.38")]     // 2-digit country
    [InlineData("98Q96B.00000.LE.3845")]   // 4-digit country
    [InlineData("98Q96.00000.LE.384")]     // 5-char FATCA id
    [InlineData("98Q96B.0000.LE.384")]     // 4-char type code
    [InlineData("98Q96B.00000.L1.384")]    // digit in the category
    [InlineData("98Q96B.00000.LE.ABC")]    // letters in the country
    [InlineData("98Q96B-00000-LE-384")]    // wrong separators
    [InlineData("98Q96B.00000.LE.384 ")]   // trailing space is trimmed, so this one passes…
    public void Rejects_a_malformed_giin(string? giin)
    {
        // …except the last case: trimming happens before the match, so a trailing space is fine.
        // Listed here deliberately so the trimming is visible rather than discovered later.
        var expected = giin == "98Q96B.00000.LE.384 ";
        ComplianceSettings.GiinIsWellFormed(giin).Should().Be(expected);
    }

    // ── Enabling FATCA ────────────────────────────────────────────────────

    [Fact]
    public void Enabling_fatca_without_a_regime_is_refused()
    {
        var result = Fresh().ConfigureFatca(
            enabled: true, regime: null, giin: ValidGiin,
            FatcaRequiredAt.Conversion, forCoHolders: true, Actor, Now);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Be("FATCA_REGIME_REQUIRED");
    }

    [Fact]
    public void Enabling_fatca_without_a_giin_is_refused()
    {
        var result = Fresh().ConfigureFatca(
            enabled: true, regime: FatcaRegime.IgaModel1, giin: null,
            FatcaRequiredAt.Conversion, forCoHolders: true, Actor, Now);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Be("FATCA_GIIN_REQUIRED");
    }

    [Fact]
    public void Enabling_fatca_with_a_malformed_giin_is_refused()
    {
        var result = Fresh().ConfigureFatca(
            enabled: true, regime: FatcaRegime.IgaModel1, giin: "NOT-A-GIIN",
            FatcaRequiredAt.Conversion, forCoHolders: true, Actor, Now);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Be("FATCA_GIIN_INVALID");
    }

    [Fact]
    public void A_refused_enable_changes_nothing()
    {
        // The gate is enabling, not saving: a half-applied FATCA configuration would publish an
        // obligation the institution cannot discharge.
        var s = Fresh();
        s.ConfigureFatca(true, null, ValidGiin, FatcaRequiredAt.Enrichment, false, Actor, Now);

        s.FatcaEnabled.Should().BeFalse();
        s.FatcaGiin.Should().BeNull();
        s.FatcaRequiredAt.Should().Be(FatcaRequiredAt.Conversion, "not even the side fields moved");
        s.FatcaForCoHolders.Should().BeTrue();
    }

    [Fact]
    public void Enabling_fatca_with_a_complete_identity_succeeds_and_upper_cases_the_giin()
    {
        var s = Fresh();
        var result = s.ConfigureFatca(
            enabled: true, regime: FatcaRegime.IgaModel2, giin: " 98q96b.00000.le.384 ",
            FatcaRequiredAt.Enrichment, forCoHolders: false, Actor, Now);

        result.IsSuccess.Should().BeTrue();
        s.FatcaEnabled.Should().BeTrue();
        s.FatcaRegime.Should().Be(FatcaRegime.IgaModel2);
        s.FatcaGiin.Should().Be("98Q96B.00000.LE.384");
        s.FatcaRequiredAt.Should().Be(FatcaRequiredAt.Enrichment);
        s.FatcaForCoHolders.Should().BeFalse();
        s.UpdatedBy.Should().Be(Actor);
    }

    // ── Disabling ─────────────────────────────────────────────────────────

    [Fact]
    public void Disabling_fatca_clears_the_institutions_identity()
    {
        var s = Fresh();
        s.ConfigureFatca(true, FatcaRegime.IgaModel1, ValidGiin,
            FatcaRequiredAt.Conversion, true, Actor, Now);

        s.ConfigureFatca(false, null, null, FatcaRequiredAt.Conversion, true, Actor, Now)
            .IsSuccess.Should().BeTrue();

        s.FatcaEnabled.Should().BeFalse();
        // A dormant GIIN is one a later re-enable would adopt silently, and the institution's
        // registration may well have changed in between.
        s.FatcaGiin.Should().BeNull();
        s.FatcaRegime.Should().BeNull();
    }

    [Fact]
    public void Disabling_ignores_a_regime_and_giin_that_are_still_posted()
    {
        // A screen that keeps its fields populated while the operator unticks the box must not
        // leave the identity behind.
        var s = Fresh();
        s.ConfigureFatca(false, FatcaRegime.IgaModel1, ValidGiin,
            FatcaRequiredAt.Conversion, true, Actor, Now);

        s.FatcaGiin.Should().BeNull();
        s.FatcaRegime.Should().BeNull();
    }

    // ── PEP window ────────────────────────────────────────────────────────

    [Theory]
    [InlineData(0)]    // "exposed only while in office" is a legitimate policy
    [InlineData(12)]
    [InlineData(600)]
    public void Accepts_a_pep_window_within_bounds(int months)
    {
        var s = Fresh();
        s.SetPepPostMandateMonths(months, Actor, Now).IsSuccess.Should().BeTrue();
        s.PepPostMandateMonths.Should().Be(months);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(601)]
    public void Refuses_a_pep_window_outside_bounds(int months)
    {
        var s = Fresh();
        var result = s.SetPepPostMandateMonths(months, Actor, Now);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Be("PEP_POST_MANDATE_MONTHS_INVALID");
        s.PepPostMandateMonths.Should().Be(12, "a refused change leaves the window alone");
    }

    [Fact]
    public void The_pep_window_is_independent_of_fatca()
    {
        // PEP vigilance binds every UEMOA institution; only FATCA depends on an intergovernmental
        // agreement. Disabling FATCA must not touch the PEP window.
        var s = Fresh();
        s.SetPepPostMandateMonths(24, Actor, Now);
        s.ConfigureFatca(false, null, null, FatcaRequiredAt.Conversion, true, Actor, Now);

        s.PepPostMandateMonths.Should().Be(24);
    }
    // ── PEP as a switch (product owner's decision, see the domain property) ──

    [Fact]
    public void A_new_row_has_PEP_vigilance_ON()
    {
        // The load-bearing default. False here would switch off an LBC/FT obligation for every
        // institution that has never opened the compliance screen.
        var s = ComplianceSettings.CreateDefault(Tenant, Actor, Now);

        s.PepEnabled.Should().BeTrue();
    }

    [Fact]
    public void PEP_can_be_switched_off_and_back_on()
    {
        var s = ComplianceSettings.CreateDefault(Tenant, Actor, Now);

        s.ConfigurePep(enabled: false, months: 12, Actor, Now).IsSuccess.Should().BeTrue();
        s.PepEnabled.Should().BeFalse();

        s.ConfigurePep(enabled: true, months: 12, Actor, Now).IsSuccess.Should().BeTrue();
        s.PepEnabled.Should().BeTrue();
    }

    [Fact]
    public void Switching_PEP_off_KEEPS_the_window()
    {
        // Unlike the FATCA identity, which is cleared on disable. A GIIN may have changed by the
        // time somebody re-enables; a window is still the institution's own choice, and clearing
        // it would make a re-enable silently adopt the platform default.
        var s = ComplianceSettings.CreateDefault(Tenant, Actor, Now);

        s.ConfigurePep(enabled: true, months: 36, Actor, Now);
        s.ConfigurePep(enabled: false, months: 36, Actor, Now);

        s.PepPostMandateMonths.Should().Be(36);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(601)]
    public void The_window_is_validated_even_while_PEP_is_OFF(int months)
    {
        // The value stored now is the one a re-enable will use, so accepting nonsense on the way
        // down only defers the refusal to the moment somebody turns it back on.
        var s = ComplianceSettings.CreateDefault(Tenant, Actor, Now);

        var result = s.ConfigurePep(enabled: false, months: months, Actor, Now);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Be("PEP_POST_MANDATE_MONTHS_INVALID");

        // And nothing moved — not the window, and not the flag.
        s.PepPostMandateMonths.Should().Be(12);
        s.PepEnabled.Should().BeTrue("a refused change leaves the pair alone");
    }
}
