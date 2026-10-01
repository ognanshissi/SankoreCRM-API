namespace Sankore.Shared.Infrastructure.Tests;

using FluentAssertions;
using Sankore.Shared.Kernel;
using Xunit;

/// <summary>
/// One rule, four callers: the email template renderer, Client, AppUser and UserProfile. They
/// each had their own version, and a client whose language read "FR" resolved no template —
/// locales are stored lower-case and matched in PostgreSQL, where equality is case-sensitive —
/// so the message went out with its own JSON payload as the body.
/// </summary>
public sealed class LanguageCodeTests
{
    [Theory]
    [InlineData("fr", "fr")]
    [InlineData("FR", "fr")]
    [InlineData("Fr", "fr")]
    [InlineData("fr-FR", "fr")]
    [InlineData("fr_FR", "fr")]
    [InlineData("FR-ci", "fr")]
    [InlineData("  EN  ", "en")]
    [InlineData("en-GB", "en")]
    [InlineData("pt-BR", "pt")]
    public void Normalize_keeps_the_primary_subtag_in_lower_case(string input, string expected)
        => LanguageCode.Normalize(input).Should().Be(expected);

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("-")]
    [InlineData("_")]
    public void Normalize_falls_back_to_french_when_there_is_nothing_usable(string? input)
        => LanguageCode.Normalize(input).Should().Be("fr").And.Be(LanguageCode.Default);

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("-")]
    public void NormalizeOrNull_keeps_absence_as_absence(string? input)
        => LanguageCode.NormalizeOrNull(input).Should().BeNull();

    [Fact]
    public void NormalizeOrNull_applies_the_same_rule_to_a_real_value()
        => LanguageCode.NormalizeOrNull("FR-ci").Should().Be("fr");

    [Fact]
    public void Normalizing_is_idempotent()
    {
        // Anything already stored must survive a second pass unchanged, otherwise a re-save
        // would keep rewriting the column.
        foreach (var input in new[] { "FR", "fr-FR", "EN", "pt-BR", "", "  " })
        {
            var once = LanguageCode.Normalize(input);
            LanguageCode.Normalize(once).Should().Be(once);
        }
    }

    [Fact]
    public void The_default_is_the_casing_email_templates_are_seeded_with()
        => LanguageCode.Default.Should().Be(LanguageCode.Default.ToLowerInvariant());
}
