namespace Sankore.Api.Tests.Infrastructure;

using FluentAssertions;
using Sankore.Api.Infrastructure;
using Xunit;

/// <summary>
/// The origin suffix rule of the free trial (<c>docs/essai-gratuit-socle.md</c>, lot 0): every
/// <c>https://&lt;name&gt;.talisoftcrm.com</c> is allowed without listing it, and nothing that merely
/// LOOKS like one.
/// </summary>
public sealed class CorsSetupTests
{
    private static readonly string[] Suffixes = [".talisoftcrm.com"];

    [Theory]
    [InlineData("https://acme.talisoftcrm.com")]
    [InlineData("https://acme.talisoftcrm.com/")]
    [InlineData("https://ACME.TalisoftCRM.com")]
    [InlineData("https://caisse-avenir-2.talisoftcrm.com")]
    [InlineData("https://acme.talisoftcrm.com:443")]
    public void An_institution_address_is_allowed(string origin) =>
        CorsSetup.IsInstitutionSubdomain(origin, Suffixes).Should().BeTrue();

    [Theory]
    [InlineData("http://acme.talisoftcrm.com")]            // plain text
    [InlineData("https://acme.talisoftcrm.com:8443")]      // another port
    [InlineData("https://talisoftcrm.com")]                // the bare domain is not an institution
    [InlineData("https://a.b.talisoftcrm.com")]            // two labels
    [InlineData("https://eviltalisoftcrm.com")]            // ends with the domain, not the suffix
    [InlineData("https://acme.talisoftcrm.com.evil.com")]  // suffix in the middle
    [InlineData("https://-acme.talisoftcrm.com")]          // not a DNS label
    [InlineData("https://acme.talisoftcrm.com/path")]
    [InlineData("https://user@acme.talisoftcrm.com")]
    [InlineData("null")]
    [InlineData("")]
    public void Anything_else_is_refused(string origin) =>
        CorsSetup.IsInstitutionSubdomain(origin, Suffixes).Should().BeFalse();

    [Fact]
    public void No_suffix_configured_allows_nothing() =>
        CorsSetup.IsInstitutionSubdomain("https://acme.talisoftcrm.com", []).Should().BeFalse();

    [Fact]
    public void Suffixes_are_normalised() =>
        CorsSetup.ParseSuffixes([" .TalisoftCRM.com ", ".talisoftcrm.com", ""])
            .Should().Equal(".talisoftcrm.com");

    [Theory]
    [InlineData("talisoftcrm.com")]   // no leading dot: would match eviltalisoftcrm.com
    [InlineData(".com")]              // a whole top-level domain
    [InlineData("*.talisoftcrm.com")]
    [InlineData(".")]
    [InlineData(".talisoft crm.com")]
    public void A_too_broad_or_malformed_suffix_fails_the_boot(string suffix)
    {
        var act = () => CorsSetup.ParseSuffixes([suffix]);
        act.Should().Throw<InvalidOperationException>().WithMessage("*AllowedOriginSuffixes*");
    }
}
