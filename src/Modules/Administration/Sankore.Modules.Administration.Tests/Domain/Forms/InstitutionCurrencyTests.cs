namespace Sankore.Modules.Administration.Tests.Domain.Forms;

using System.Text.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using Sankore.Modules.Administration.PublicApi.Forms;
using Sankore.Shared.Kernel;
using Xunit;

/// <summary>
/// One currency per institution (owner, 2026-10-24): an amount takes it, keeps the one it already
/// had before a change, and is refused in any other.
/// </summary>
public sealed class InstitutionCurrencyTests
{
    [Theory]
    [InlineData(null, "EUR", null, "EUR")]   // none given: the institution's
    [InlineData("eur", "EUR", null, "EUR")]
    [InlineData("XOF", "EUR", "XOF", "XOF")] // typed before the change: keeps its own
    [InlineData(null, "", null, "XOF")]      // no readable institution: the platform default
    public void An_amount_takes_the_institution_currency_or_keeps_its_own(
        string? requested, string institution, string? stored, string expected)
        => InstitutionCurrencies.Resolve(requested, institution, stored).Value.Should().Be(expected);

    [Theory]
    [InlineData("USD", "EUR", null)]
    [InlineData("USD", "EUR", "XOF")]
    public void Any_other_currency_is_refused(string requested, string institution, string? stored)
        => InstitutionCurrencies.Resolve(requested, institution, stored).Error
            .Should().Be(InstitutionCurrencies.NotInstitution);

    // ── Custom Money fields: the form carries the currency ─────────────────────

    private static readonly IReadOnlyDictionary<string, string?> NoNatives = new Dictionary<string, string?>();

    private static EntityFormDto Form(string? currency) => new(
        FormEntity.Lead, 1, [new FormSection("main", "Principal", 1)],
        [new FormField("budget", FormFieldType.Money, "Budget", 1, "main")], currency);

    private static Dictionary<string, JsonElement> Values(object values)
        => JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(JsonSerializer.Serialize(values))!;

    private static string CurrencyOf(string? json) => ((JsonObject)JsonNode.Parse(json!)!)["budget"]!["currency"]!.GetValue<string>();

    [Fact]
    public void A_bare_amount_takes_the_form_currency()
    {
        var result = CustomFieldValues.Apply(Form("EUR"), null, Values(new { budget = "1 500" }),
            NoNatives, enforceRequired: false, isCreate: true);

        CurrencyOf(result.Value).Should().Be("EUR");
    }

    [Fact]
    public void An_amount_in_another_currency_is_refused()
    {
        var result = CustomFieldValues.Apply(Form("EUR"), null, Values(new { budget = new { amount = 1500, currency = "XOF" } }),
            NoNatives, enforceRequired: false, isCreate: true);

        result.Error.Should().Be("CUSTOM_FIELD_CURRENCY_NOT_INSTITUTION:budget");
    }

    [Fact]
    public void An_amount_stored_before_a_change_keeps_its_currency_on_edit()
    {
        const string stored = """{"budget":{"amount":1000,"currency":"XOF"}}""";

        var result = CustomFieldValues.Apply(Form("EUR"), stored, Values(new { budget = new { amount = 2000, currency = "XOF" } }),
            NoNatives, enforceRequired: false, isCreate: false);

        result.IsSuccess.Should().BeTrue(result.Error);
        CurrencyOf(result.Value).Should().Be("XOF");
    }

    [Fact]
    public void A_form_without_currency_accepts_any_as_before()
        => CustomFieldValues.Apply(Form(null), null, Values(new { budget = new { amount = 1500, currency = "USD" } }),
            NoNatives, enforceRequired: false, isCreate: true).IsSuccess.Should().BeTrue();
}
