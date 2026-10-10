namespace Sankore.Shared.Infrastructure.Tests.Person;

using FluentAssertions;
using Sankore.Shared.Kernel.ValueObject;
using Xunit;

/// <summary>
/// <see cref="IsoCountry"/> derives its set from the runtime's own region data instead of a table
/// checked into the repository. These cases exist because that is a bet on the host's ICU build —
/// so the countries this product actually operates in are pinned explicitly.
/// </summary>
public sealed class IsoCountryTests
{
    [Theory]
    [InlineData("CI")]  // Côte d'Ivoire — priority zone
    [InlineData("SN")]  // Sénégal
    [InlineData("ML")]  // Mali
    [InlineData("BF")]  // Burkina Faso
    [InlineData("BJ")]  // Bénin
    [InlineData("TG")]  // Togo
    [InlineData("NE")]  // Niger
    [InlineData("GW")]  // Guinée-Bissau
    [InlineData("GH")]  // Ghana
    [InlineData("NG")]  // Nigeria
    [InlineData("CD")]  // RD Congo
    [InlineData("FR")]  // France — phase 2
    [InlineData("BE")]
    [InlineData("US")]  // needed by FATCA
    public void Recognizes_every_country_this_product_operates_in(string code)
    {
        IsoCountry.IsKnown(code).Should().BeTrue();
        IsoCountry.IsKnown(code.ToLowerInvariant()).Should().BeTrue("the check is case-insensitive");
    }

    [Fact]
    public void The_eight_uemoa_members_are_all_recognized()
    {
        // Asserted as a set as well as individually: a host with region data so thin that only
        // some of them resolve would pass the theory above for the ones it has and silently
        // reject the others at validation time.
        string[] uemoa = ["BJ", "BF", "CI", "GW", "ML", "NE", "SN", "TG"];
        uemoa.Should().OnlyContain(c => IsoCountry.IsKnown(c));
    }

    [Theory]
    [InlineData("CIV")]
    [InlineData("C")]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("1A")]
    [InlineData("C-")]
    public void Rejects_anything_that_is_not_two_letters(string? code)
    {
        IsoCountry.HasValidShape(code).Should().BeFalse();
        IsoCountry.IsKnown(code).Should().BeFalse();
    }

    [Fact]
    public void A_well_shaped_code_that_is_not_a_country_is_not_known()
    {
        IsoCountry.HasValidShape("ZZ").Should().BeTrue("ZZ is two letters");
        IsoCountry.IsKnown("ZZ").Should().BeFalse("but it is not an assigned ISO 3166-1 country");
    }

    [Fact]
    public void Normalize_upper_cases_and_collapses_blanks()
    {
        IsoCountry.Normalize(" ci ").Should().Be("CI");
        IsoCountry.Normalize("   ").Should().BeNull();
        IsoCountry.Normalize(null).Should().BeNull();
    }
}
