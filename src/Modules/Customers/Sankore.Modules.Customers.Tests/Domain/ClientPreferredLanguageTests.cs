namespace Sankore.Modules.Customers.Tests.Domain;

using FluentAssertions;
using Sankore.Modules.Customers.Domain;
using Sankore.Modules.Customers.Tests.TestSupport;
using Xunit;

/// <summary>
/// The column used to accept "fr", "Fr" and "FR" as three different languages — the aggregate
/// only trimmed, and its own fallback was "FR" upper-case. It surfaced as an email: template
/// locales are stored lower-case and resolved in PostgreSQL, where equality is case-sensitive,
/// so a client whose language read "FR" matched no template and was sent the message's own JSON
/// payload as its body.
/// </summary>
public sealed class ClientPreferredLanguageTests
{
    private readonly Guid _tenantId = Guid.NewGuid();
    private readonly Guid _agencyId = Guid.NewGuid();

    private Client Individual(string language) =>
        TestClientFactory.Individual(_tenantId, _agencyId, preferredLanguage: language);

    private Client Legal(string language) =>
        TestClientFactory.Legal(_tenantId, _agencyId, preferredLanguage: language);

    [Theory]
    [InlineData("FR", "fr")]
    [InlineData("Fr", "fr")]
    [InlineData("fr", "fr")]
    [InlineData("fr-FR", "fr")]
    [InlineData("fr_FR", "fr")]
    [InlineData("  FR  ", "fr")]
    [InlineData("EN", "en")]
    [InlineData("en-GB", "en")]
    public void An_individual_stores_one_normalised_language(string input, string expected)
        => Individual(input).PreferredLanguage.Should().Be(expected);

    [Theory]
    [InlineData("FR", "fr")]
    [InlineData("EN", "en")]
    [InlineData("pt-BR", "pt")]
    public void A_legal_entity_stores_one_normalised_language(string input, string expected)
        => Legal(input).PreferredLanguage.Should().Be(expected);

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void A_blank_language_falls_back_to_the_system_default(string input)
    {
        Individual(input).PreferredLanguage
            .Should().Be(Client.DefaultLanguageCode).And.Be("fr");
    }

    [Fact]
    public void Updating_the_language_normalises_it_too()
    {
        // Normalising only on creation would let the next edit put "FR" back in the column.
        var client = Individual("fr");

        var result = client.UpdateNonSensitive(
            profession: null,
            employer: null,
            maritalStatus: null,
            encryptedDeclaredIncome: null,
            declaredIncomeCurrency: null,
            preferredLanguage: "FR",
            actor: Guid.NewGuid());

        result.IsSuccess.Should().BeTrue();
        client.PreferredLanguage.Should().Be("fr");
    }

    [Fact]
    public void Updating_with_a_blank_language_leaves_the_stored_one_alone()
    {
        var client = Individual("en");

        client.UpdateNonSensitive(
            profession: "Commerçante",
            employer: null,
            maritalStatus: null,
            encryptedDeclaredIncome: null,
            declaredIncomeCurrency: null,
            preferredLanguage: null,
            actor: Guid.NewGuid());

        client.PreferredLanguage.Should().Be("en");
    }

    [Fact]
    public void Every_stored_language_matches_the_casing_email_templates_are_seeded_with()
    {
        // The contract that was broken: whatever lands here must resolve a template.
        string[] inputs = ["FR", "Fr", "fr-FR", "EN", "en-GB", "", "  "];

        var stored = inputs.Select(i => Individual(i).PreferredLanguage).ToList();

        stored.Should().OnlyContain(l => l == l.ToLowerInvariant());
        stored.Should().OnlyContain(l => !l.Contains('-') && !l.Contains('_'));
    }
}
