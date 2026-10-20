namespace Sankore.Modules.Administration.Tests.Domain.Forms;

using System.Text.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using Sankore.Modules.Administration.PublicApi.Forms;
using Xunit;

/// <summary>
/// What a spreadsheet cell, a web form or a list filter sends — text — read with the same rules as
/// a typed write (dynamic forms, lot 6).
/// </summary>
public sealed class CustomFieldTextAndFilterTests
{
    private static readonly EntityFormDto Form = new(FormEntity.Lead, 3, [],
    [
        new FormField("segment", FormFieldType.SingleChoice, "Segment", 1,
            Options: [new FormOption("Commercant", "Commerçant"), new FormOption("Salarie", "Salarié")]),
        new FormField("sectors", FormFieldType.MultiChoice, "Secteurs", 2,
            Options: [new FormOption("agri", "Agriculture"), new FormOption("com", "Commerce")]),
        new FormField("vip", FormFieldType.Boolean, "VIP", 3),
        new FormField("budget", FormFieldType.Money, "Budget", 4),
        new FormField("since", FormFieldType.Date, "Client depuis", 5),
        new FormField("rate", FormFieldType.Decimal, "Taux", 6),
        new FormField("spouse", FormFieldType.Text, "Conjoint", 7, IsSensitive: true),
        new FormField("phoneNumber", FormFieldType.Phone, "Téléphone", 8, IsNative: true),
    ]);

    private static JsonObject Store(Dictionary<string, string?> cells)
    {
        var result = CustomFieldValues.Apply(Form, null, CustomFieldValues.FromText(cells), null, false, true);
        result.IsSuccess.Should().BeTrue(result.Error);
        return (JsonObject)JsonNode.Parse(result.Value!)!;
    }

    [Fact]
    public void Text_cells_are_read_as_a_person_would_type_them()
    {
        var stored = Store(new()
        {
            ["custom.segment"] = "commerçant",
            ["custom.sectors"] = "Agriculture; com",
            ["custom.vip"] = "Oui",
            ["custom.budget"] = "1 500 000 XOF",
            ["custom.since"] = "01/03/2026",
            ["custom.rate"] = "12,5",
            ["Notes"] = "pas une colonne personnalisée",
            ["custom.empty"] = "  ",
        });

        stored["segment"]!.GetValue<string>().Should().Be("Commercant");
        stored["sectors"]!.AsArray().Select(n => n!.GetValue<string>()).Should().Equal("agri", "com");
        stored["vip"]!.GetValue<bool>().Should().BeTrue();
        stored["budget"]!["amount"]!.GetValue<decimal>().Should().Be(1_500_000);
        stored["budget"]!["currency"]!.GetValue<string>().Should().Be("XOF");
        stored["since"]!.GetValue<string>().Should().Be("2026-03-01");
        stored["rate"]!.GetValue<decimal>().Should().Be(12.5m);
        stored.ContainsKey("empty").Should().BeFalse("a blank cell means no value, not an unknown field");
    }

    [Fact]
    public void A_header_in_another_casing_lands_on_its_field()
    {
        Store(new() { ["CUSTOM.Segment"] = "Salarié" })["segment"]!.GetValue<string>().Should().Be("Salarie");
    }

    [Fact]
    public void Best_effort_keeps_what_the_form_accepts_and_names_the_rest()
    {
        var (json, rejected) = CustomFieldValues.ApplyBestEffort(Form, null, CustomFieldValues.FromText(new Dictionary<string, string?>
        {
            ["custom.segment"] = "Fonctionnaire",
            ["custom.vip"] = "oui",
            ["custom.nowhere"] = "x",
        }), null);

        rejected.Should().BeEquivalentTo("segment", "nowhere");
        JsonNode.Parse(json!)!["vip"]!.GetValue<bool>().Should().BeTrue();
    }

    [Fact]
    public void An_import_report_line_names_the_column_to_fix()
    {
        CustomFieldValues.DescribeForImport("CUSTOM_FIELD_UNKNOWN_OPTION:segment")
            .Should().Be("Colonne custom.segment : valeur absente des options du champ.");
        CustomFieldValues.DescribeForImport("NAME_REQUIRED").Should().BeNull();
    }

    // ── Filters ──────────────────────────────────────────────────────────

    [Theory]
    [InlineData("segment:Commerçant", """{"segment":"Commercant"}""")]
    [InlineData("vip:oui", """{"vip":true}""")]
    [InlineData("sectors:Agriculture", """{"sectors":["agri"]}""")]
    public void A_filter_becomes_the_document_a_jsonb_containment_matches(string filter, string expected)
    {
        var documents = CustomFieldValues.FilterDocuments(Form, [filter]);

        documents.IsSuccess.Should().BeTrue(documents.Error);
        JsonNode.DeepEquals(JsonNode.Parse(documents.Value.Single()), JsonNode.Parse(expected)).Should().BeTrue();
    }

    [Theory]
    [InlineData("spouse:Koné", "CUSTOM_FILTER_SENSITIVE:spouse")]
    [InlineData("nowhere:x", "CUSTOM_FILTER_UNKNOWN:nowhere")]
    [InlineData("phoneNumber:0700000000", "CUSTOM_FILTER_UNKNOWN:phoneNumber")]
    [InlineData("segment:Fonctionnaire", "CUSTOM_FILTER_INVALID:segment")]
    [InlineData("segment", "CUSTOM_FILTER_INVALID:segment")]
    public void A_filter_that_cannot_be_honoured_is_refused_rather_than_ignored(string filter, string error)
    {
        CustomFieldValues.FilterDocuments(Form, [filter]).Error.Should().Be(error);
    }
}
