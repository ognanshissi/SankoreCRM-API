namespace Sankore.Modules.Administration.Tests.Domain;

using FluentAssertions;
using Sankore.Modules.Administration.Domain;
using Sankore.Shared.Kernel;
using Xunit;

/// <summary>
/// M12 stored its language codes verbatim, like M01 did before the fix. No email was broken by it
/// — the renderer now normalises on read — so this is the write side catching up, so that one
/// column holds one value per language.
/// </summary>
public sealed class AppUserLanguageTests
{
    private static AppUser User() =>
        AppUser.Create(Guid.NewGuid(), Guid.NewGuid(), "Awa", "Ouattara", "awa@mfi.ci");

    [Theory]
    [InlineData("FR", "fr")]
    [InlineData("Fr", "fr")]
    [InlineData("fr-FR", "fr")]
    [InlineData("EN", "en")]
    public void A_preferred_language_is_stored_normalised(string input, string expected)
    {
        var user = User();

        user.SetPreferredLanguage(input);

        user.PreferredLanguage.Should().Be(expected);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void No_preference_stays_no_preference(string? input)
    {
        // Not "fr": null means "use the tenant default", which ModuleEmailSender relies on.
        var user = User();
        user.SetPreferredLanguage("en");

        user.SetPreferredLanguage(input);

        user.PreferredLanguage.Should().BeNull();
    }

    [Fact]
    public void An_agents_spoken_languages_are_stored_normalised_and_deduplicated()
    {
        var agent = AppUser.CreateAgent(
            Guid.NewGuid(), Guid.NewGuid(), "Koffi", "Kouassi", "koffi@mfi.ci",
            languages: ["FR", "fr-FR", "  en  ", "EN", "dioula"],
            specialties: []);

        agent.SpokenLanguages.Should().Equal("fr", "en", "dioula");
    }

    [Fact]
    public void Updating_the_spoken_languages_normalises_them_too()
    {
        var agent = AppUser.CreateAgent(
            Guid.NewGuid(), Guid.NewGuid(), "Koffi", "Kouassi", "koffi@mfi.ci",
            languages: ["fr"], specialties: []);

        agent.UpdateDetails(
            fullName: null,
            agencyId: null,
            spokenLanguages: ["FR", "EN-gb"],
            specialties: null,
            enableNotifications: null);

        agent.SpokenLanguages.Should().Equal("fr", "en");
    }

    [Fact]
    public void A_user_profile_stores_its_default_language_normalised()
    {
        var profile = UserProfile.Create(Guid.NewGuid(), Guid.NewGuid(), "FR");

        profile.DefaultLanguage.Should().Be(LanguageCode.Default).And.Be("fr");
    }
}
