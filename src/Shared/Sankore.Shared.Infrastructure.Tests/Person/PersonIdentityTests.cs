namespace Sankore.Shared.Infrastructure.Tests.Person;

using FluentAssertions;
using Sankore.Shared.Kernel;
using Sankore.Shared.Kernel.ValueObject;
using Xunit;

/// <summary>Covers <see cref="PersonIdentity"/> (BNF-B-02).</summary>
public sealed class PersonIdentityTests
{
    private static PersonIdentity Principal() => PersonIdentity.Create(
        lastName:    "  Kouassi ",
        firstName:   " Aya  ",
        birthDate:   new DateOnly(1987, 4, 2),
        gender:      PersonGender.Female,
        addressLine: " Rue des Jardins 12 ",
        postalCode:  " 01BP1234 ",
        city:        " Abidjan ",
        countryCode: "ci");

    [Fact]
    public void Create_trims_and_normalizes()
    {
        var p = Principal();

        p.LastName.Should().Be("Kouassi");
        p.FirstName.Should().Be("Aya");
        p.AddressLine.Should().Be("Rue des Jardins 12");
        p.City.Should().Be("Abidjan");
        p.CountryCode.Should().Be("CI", "country codes are stored upper-cased");
        p.FullName.Should().Be("Aya Kouassi");
    }

    [Theory]
    [InlineData("", "Dupont")]
    [InlineData("   ", "Dupont")]
    [InlineData("Dupont", "")]
    [InlineData("Dupont", "  ")]
    public void Create_refuses_a_blank_name(string lastName, string firstName)
    {
        // Every other field is optional; these two are not. An owned type whose every column is
        // null is one EF cannot tell from an absent owner.
        var act = () => PersonIdentity.Create(lastName, firstName);
        act.Should().Throw<DomainException>();
    }

    [Theory]
    [InlineData("CIV")]
    [InlineData("C")]
    [InlineData("C1")]
    public void Create_refuses_a_country_code_that_is_not_two_letters(string code)
    {
        var act = () => PersonIdentity.Create("Kouassi", "Aya", countryCode: code);
        act.Should().Throw<DomainException>();
    }

    [Fact]
    public void Create_treats_a_blank_country_code_as_absent()
    {
        PersonIdentity.Create("Kouassi", "Aya", countryCode: "   ").CountryCode.Should().BeNull();
    }

    [Fact]
    public void Create_accepts_a_country_the_runtime_may_not_know()
    {
        // The shape is the value object's business; whether the two letters name a real country is
        // the validator's. A value object must not fail on a host whose globalization data is
        // trimmed — see IsoCountry.
        PersonIdentity.Create("Kouassi", "Aya", countryCode: "ZZ").CountryCode.Should().Be("ZZ");
    }

    [Fact]
    public void WithAddressOf_takes_the_whole_postal_block_and_nothing_else()
    {
        var child = PersonIdentity.Create("Kouassi", "Kofi", birthDate: new DateOnly(2015, 6, 1));
        var resolved = child.WithAddressOf(Principal());

        resolved.AddressLine.Should().Be("Rue des Jardins 12");
        resolved.PostalCode.Should().Be("01BP1234");
        resolved.City.Should().Be("Abidjan");
        resolved.CountryCode.Should().Be("CI");

        // The inheritance is an address link, not an identity copy.
        resolved.FirstName.Should().Be("Kofi");
        resolved.BirthDate.Should().Be(new DateOnly(2015, 6, 1));
    }

    [Fact]
    public void WithAddressOf_does_not_mutate_the_member()
    {
        var child = PersonIdentity.Create("Kouassi", "Kofi");
        _ = child.WithAddressOf(Principal());

        child.AddressLine.Should().BeNull(
            "UsesPrincipalAddress resolves at read time; the member's own row stays empty");
    }

    [Theory]
    [InlineData("2026-04-01", 38)]   // the day before the birthday
    [InlineData("2026-04-02", 39)]   // the birthday itself
    [InlineData("2026-04-03", 39)]
    public void AgeAt_counts_whole_years(string asOf, int expected)
    {
        Principal().AgeAt(DateOnly.Parse(asOf, System.Globalization.CultureInfo.InvariantCulture))
            .Should().Be(expected);
    }

    [Fact]
    public void AgeAt_is_unknown_without_a_birth_date()
    {
        PersonIdentity.Create("Kouassi", "Aya").AgeAt(new DateOnly(2026, 1, 1)).Should().BeNull();
    }

    [Fact]
    public void Anonymized_blanks_every_nominative_field_but_keeps_the_country()
    {
        var a = Principal().Anonymized();

        a.LastName.Should().Be("ANONYMIZED");
        a.FirstName.Should().Be("ANONYMIZED");
        a.BirthDate.Should().BeNull();
        a.AddressLine.Should().BeNull();
        a.PostalCode.Should().BeNull();
        a.City.Should().BeNull();
        a.UsageName.Should().BeNull();
        a.Gender.Should().Be(PersonGender.Unknown);

        // A country is not nominative and reporting still needs it after anonymisation (BNF-B-19).
        a.CountryCode.Should().Be("CI");
    }

    [Fact]
    public void An_empty_french_block_is_stored_as_absent()
    {
        // Phase 2 (BNF-B-21). Mapped now so adding it later is not a migration on every table,
        // written by nothing — so an all-empty block must not produce a row's worth of columns.
        PersonIdentity.Create("Kouassi", "Aya", frenchSocialSecurity: new FrenchSocialSecurity())
            .FrenchSocialSecurity.Should().BeNull();
    }

    [Fact]
    public void Unknown_is_the_default_gender()
    {
        // It is first in the enum on purpose: an owned type whose columns were never written must
        // not read as a person's gender picked by field order.
        default(PersonGender).Should().Be(PersonGender.Unknown);
        PersonIdentity.Create("Kouassi", "Aya").Gender.Should().Be(PersonGender.Unknown);
    }
}
