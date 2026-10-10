namespace Sankore.Shared.Infrastructure.Tests.Person;

using System.Globalization;
using FluentAssertions;
using Sankore.Shared.Infrastructure.Validation;
using Sankore.Shared.Kernel.ValueObject;
using Xunit;

/// <summary>
/// Covers the shared validators (BNF-B-02). They are the request-shaped rules; the invariants the
/// value objects enforce by throwing are covered in <see cref="PersonIdentityTests"/> and
/// <see cref="PepStatusTests"/>.
/// </summary>
public sealed class PersonValidatorTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 10, 10, 12, 0, 0, TimeSpan.Zero);

    private static TimeProvider Clock() => new FrozenClock(Now);

    /// <summary>
    /// Local rather than Microsoft.Extensions.TimeProvider.Testing: nothing in this solution
    /// references that package, and the KYC tests already roll the same three lines. Adding a
    /// dependency to freeze one instant is not worth the restore.
    /// </summary>
    private sealed class FrozenClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private static PersonIdentityValidator IdentityValidator() => new(Clock());
    private static PepStatusValidator PepValidator() => new(Clock());

    // ── PersonIdentity ────────────────────────────────────────────────────

    [Fact]
    public void Accepts_a_complete_west_african_identity()
    {
        var p = PersonIdentity.Create(
            "Kouassi", "Aya", birthDate: new DateOnly(1987, 4, 2), countryCode: "CI");

        IdentityValidator().Validate(p).IsValid.Should().BeTrue();
    }

    [Theory]
    [InlineData("N'Guessan")]   // apostrophe
    [InlineData("N’Guessan")]   // typographic apostrophe
    [InlineData("Diallo-Bâ")]   // hyphen and a circumflex
    [InlineData("Köhler")]      // diaeresis
    [InlineData("Jean Paul")]   // space
    [InlineData("St. John")]    // period
    public void Accepts_the_name_forms_this_region_actually_uses(string lastName)
    {
        var p = PersonIdentity.Create(lastName, "Aya");
        IdentityValidator().Validate(p).IsValid.Should().BeTrue($"'{lastName}' is a real surname shape");
    }

    [Theory]
    [InlineData("Kouassi2")]
    [InlineData("<script>")]
    [InlineData("-Kouassi")]    // must start with a letter
    public void Rejects_a_name_that_is_not_a_name(string lastName)
    {
        var p = PersonIdentity.Create(lastName, "Aya");
        IdentityValidator().Validate(p).IsValid.Should().BeFalse();
    }

    [Fact]
    public void Rejects_a_birth_date_in_the_future()
    {
        var p = PersonIdentity.Create("Kouassi", "Aya", birthDate: new DateOnly(2026, 10, 11));

        var result = IdentityValidator().Validate(p);
        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.ErrorMessage.Contains("future"));
    }

    [Fact]
    public void Accepts_a_birth_date_of_today()
    {
        // The boundary is inclusive: a newborn added to a household on the day of birth.
        var p = PersonIdentity.Create("Kouassi", "Kofi", birthDate: new DateOnly(2026, 10, 10));
        IdentityValidator().Validate(p).IsValid.Should().BeTrue();
    }

    [Fact]
    public void Rejects_an_implausible_birth_date()
    {
        var p = PersonIdentity.Create("Kouassi", "Aya", birthDate: new DateOnly(1899, 12, 31));
        IdentityValidator().Validate(p).IsValid.Should().BeFalse();
    }

    [Fact]
    public void Rejects_a_country_code_that_is_not_a_real_country()
    {
        // The value object accepted ZZ — two letters is all it checks. The validator is where a
        // wrong country is worth a 400.
        var p = PersonIdentity.Create("Kouassi", "Aya", countryCode: "ZZ");
        IdentityValidator().Validate(p).IsValid.Should().BeFalse();
    }

    [Fact]
    public void Refuses_a_populated_french_social_security_block()
    {
        // BNF-B-21 is phase 2. Accepting an unverified NIR now would put it into a blind index the
        // dedup then trusts. The gate is here rather than in the value object so the day phase 2
        // lands, one rule is removed instead of a type being reopened.
        var p = PersonIdentity.Create(
            "Durand", "Marie",
            frenchSocialSecurity: new FrenchSocialSecurity(Nir: "287042A12345678"));

        IdentityValidator().Validate(p).IsValid.Should().BeFalse();
    }

    // ── PepStatus ─────────────────────────────────────────────────────────

    [Fact]
    public void Accepts_a_person_who_is_not_exposed()
    {
        PepValidator().Validate(PepStatus.NotExposed).IsValid.Should().BeTrue();
    }

    [Fact]
    public void Accepts_a_complete_exposure()
    {
        var s = PepStatus.Exposed("Ministre des Finances", PepLink.Self, countryCode: "CI");
        PepValidator().Validate(s).IsValid.Should().BeTrue();
    }

    [Fact]
    public void Rejects_a_function_end_date_in_the_future()
    {
        // A mandate that has not ended is a null date, not a future one — a future date would
        // start the post-mandate countdown from a day that has not happened, so the person would
        // read as exposed for the window PLUS the gap.
        var s = PepStatus.Exposed(
            "Ministre", PepLink.Self, functionEndDate: new DateOnly(2026, 10, 11));

        PepValidator().Validate(s).IsValid.Should().BeFalse();
    }

    [Fact]
    public void Rejects_an_overlong_function_title()
    {
        var s = PepStatus.Exposed(new string('x', 151), PepLink.Self);
        PepValidator().Validate(s).IsValid.Should().BeFalse();
    }

    [Fact]
    public void Dates_are_parsed_the_same_way_whatever_the_host_culture()
    {
        // The validators compare DateOnly values, so no parsing is involved — asserted rather than
        // assumed, because the same claim was wrong for the spreadsheet readers and the failure
        // was invisible on an invariant-culture machine.
        var original = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("fr-FR");

            var p = PersonIdentity.Create("Kouassi", "Aya", birthDate: new DateOnly(1987, 4, 2));
            IdentityValidator().Validate(p).IsValid.Should().BeTrue();
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }
}
