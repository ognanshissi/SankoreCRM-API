namespace Sankore.Modules.Administration.Tests.Domain.Forms;

using System.Text.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using Sankore.Modules.Administration.PublicApi.Forms;
using Xunit;

/// <summary>
/// The one definition of "is this custom value acceptable", shared by leads, clients and products.
/// </summary>
public sealed class CustomFieldValuesTests
{
    private static readonly IReadOnlyDictionary<string, string?> NoNatives = new Dictionary<string, string?>();

    private static EntityFormDto Form(params FormField[] fields)
        => new(FormEntity.Lead, 1, [new FormSection("main", "Principal", 1)], fields);

    private static FormField Field(string key, FormFieldType type, bool required = false, params string[] options)
        => new(key, type, key, 1, "main", QuickAdd: required, IsRequired: required,
            Options: options.Length == 0 ? null : options.Select(o => new FormOption(o, o)).ToList());

    private static Dictionary<string, JsonElement> Values(object values)
        => JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(JsonSerializer.Serialize(values))!;

    private static JsonObject Stored(string? json) => (JsonObject)JsonNode.Parse(json!)!;

    [Fact]
    public void Each_type_is_checked_and_normalised()
    {
        var form = Form(
            Field("label", FormFieldType.Text),
            Field("count", FormFieldType.Integer),
            Field("rate", FormFieldType.Percent),
            Field("budget", FormFieldType.Money),
            Field("since", FormFieldType.Date),
            Field("vip", FormFieldType.Boolean),
            Field("segment", FormFieldType.SingleChoice, false, "A", "B"),
            Field("tags", FormFieldType.MultiChoice, false, "x", "y"),
            Field("site", FormFieldType.Url),
            Field("mail", FormFieldType.Email));

        var result = CustomFieldValues.Apply(form, null, Values(new
        {
            label = "  Marché de Treichville ",
            count = "3",
            rate = 12.5,
            budget = new { amount = 1500000, currency = "xof" },
            since = "2026-03-01",
            vip = "true",
            segment = "B",
            tags = new[] { "y", "x", "y" },
            site = "https://example.ci",
            mail = "awa@example.ci",
        }), NoNatives, enforceRequired: false, isCreate: true);

        result.IsSuccess.Should().BeTrue(result.Error);
        var stored = Stored(result.Value);
        stored["label"]!.GetValue<string>().Should().Be("Marché de Treichville");
        stored["count"]!.GetValue<decimal>().Should().Be(3);
        stored["budget"]!["currency"]!.GetValue<string>().Should().Be("XOF");
        stored["vip"]!.GetValue<bool>().Should().BeTrue();
        stored["tags"]!.AsArray().Select(n => n!.GetValue<string>()).Should().Equal("y", "x");
    }

    [Theory]
    [InlineData("count", "\"3.5\"", "CUSTOM_FIELD_INVALID:count")]
    [InlineData("since", "\"31/13/2026\"", "CUSTOM_FIELD_INVALID:since")]
    [InlineData("segment", "\"C\"", "CUSTOM_FIELD_UNKNOWN_OPTION:segment")]
    [InlineData("rate", "120", "CUSTOM_FIELD_OUT_OF_RANGE:rate")]
    [InlineData("site", "\"javascript:alert(1)\"", "CUSTOM_FIELD_INVALID:site")]
    [InlineData("mail", "\"pas-un-email\"", "CUSTOM_FIELD_INVALID:mail")]
    [InlineData("budget", "{\"amount\":10,\"currency\":\"EURO\"}", "CUSTOM_FIELD_INVALID:budget")]
    public void A_value_its_type_refuses_names_the_field(string key, string json, string error)
    {
        var form = Form(
            Field("count", FormFieldType.Integer),
            Field("since", FormFieldType.Date),
            Field("segment", FormFieldType.SingleChoice, false, "A", "B"),
            Field("rate", FormFieldType.Percent),
            Field("site", FormFieldType.Url),
            Field("mail", FormFieldType.Email),
            Field("budget", FormFieldType.Money));

        var changes = new Dictionary<string, JsonElement> { [key] = JsonDocument.Parse(json).RootElement };

        CustomFieldValues.Apply(form, null, changes, NoNatives, false, true)
            .Error.Should().Be(error);
    }

    [Fact]
    public void An_unknown_key_and_an_archived_field_are_refused()
    {
        var form = Form(Field("segment", FormFieldType.Text) with { IsArchived = true });

        CustomFieldValues.Apply(form, null, Values(new { other = "x" }), NoNatives, false, true)
            .Error.Should().Be("CUSTOM_FIELD_UNKNOWN:other");
        CustomFieldValues.Apply(form, null, Values(new { segment = "x" }), NoNatives, false, true)
            .Error.Should().Be("CUSTOM_FIELD_ARCHIVED:segment");
    }

    [Fact]
    public void A_native_key_is_not_a_custom_value()
    {
        var form = Form(Field("phoneNumber", FormFieldType.Phone) with { IsNative = true });

        CustomFieldValues.Apply(form, null, Values(new { phoneNumber = "0700000000" }), NoNatives, false, true)
            .Error.Should().Be("CUSTOM_FIELD_UNKNOWN:phoneNumber");
    }

    [Fact]
    public void An_update_merges_a_null_clears_and_an_absent_key_is_kept()
    {
        var form = Form(Field("a", FormFieldType.Text), Field("b", FormFieldType.Text), Field("c", FormFieldType.Text));
        const string current = """{"a":"1","b":"2"}""";

        var changes = new Dictionary<string, JsonElement>
        {
            ["b"] = JsonDocument.Parse("null").RootElement,
            ["c"] = JsonDocument.Parse("\"3\"").RootElement,
        };

        var stored = Stored(CustomFieldValues.Apply(form, current, changes, NoNatives, true, false).Value);

        stored.Select(p => p.Key).Should().BeEquivalentTo("a", "c");
        stored["a"]!.GetValue<string>().Should().Be("1");
    }

    [Fact]
    public void Nothing_stored_is_null_rather_than_an_empty_object()
    {
        var form = Form(Field("a", FormFieldType.Text));

        CustomFieldValues.Apply(form, """{"a":"1"}""", Values(new { a = (string?)null }), NoNatives, false, false)
            .Value.Should().BeNull();
    }

    [Fact]
    public void A_creation_on_an_interactive_path_requires_every_required_field()
    {
        var form = Form(Field("segment", FormFieldType.Text, required: true));

        CustomFieldValues.Apply(form, null, null, NoNatives, enforceRequired: true, isCreate: true)
            .Error.Should().Be("CUSTOM_FIELD_REQUIRED:segment");
    }

    [Fact]
    public void An_import_is_not_refused_for_a_field_it_has_never_heard_of()
    {
        var form = Form(Field("segment", FormFieldType.Text, required: true));

        CustomFieldValues.Apply(form, null, null, NoNatives, enforceRequired: false, isCreate: true)
            .IsSuccess.Should().BeTrue();
    }

    [Fact]
    public void An_update_is_refused_only_for_clearing_a_required_field_not_for_one_never_filled()
    {
        var form = Form(Field("segment", FormFieldType.Text, required: true), Field("note", FormFieldType.Text));

        // A field made required after the record was created does not block an unrelated edit…
        CustomFieldValues.Apply(form, null, Values(new { note = "x" }), NoNatives, true, isCreate: false)
            .IsSuccess.Should().BeTrue();

        // …but it may not be cleared.
        CustomFieldValues.Apply(form, """{"segment":"A"}""", Values(new { segment = (string?)null }), NoNatives, true, false)
            .Error.Should().Be("CUSTOM_FIELD_REQUIRED:segment");
    }

    [Fact]
    public void A_required_field_hidden_by_its_condition_is_not_required()
    {
        var companyOnly = Field("sector", FormFieldType.Text, required: true)
            with { VisibleWhen = new FormCondition("prospectType", ["Corporate"]) };
        var form = Form(companyOnly);

        CustomFieldValues.Apply(form, null, null,
                new Dictionary<string, string?> { ["prospectType"] = "Individual" }, true, true)
            .IsSuccess.Should().BeTrue();

        CustomFieldValues.Apply(form, null, null,
                new Dictionary<string, string?> { ["prospectType"] = "Corporate" }, true, true)
            .Error.Should().Be("CUSTOM_FIELD_REQUIRED:sector");
    }

    [Fact]
    public void A_condition_on_a_section_hides_its_fields_too()
    {
        var form = new EntityFormDto(FormEntity.Client, 1,
            [new FormSection("pm", "Personne morale", 1, VisibleWhen: new FormCondition("clientType", ["Legal"]))],
            [Field("capital", FormFieldType.Money, required: true) with { SectionKey = "pm" }]);

        CustomFieldValues.Apply(form, null, null,
                new Dictionary<string, string?> { ["clientType"] = "Individual" }, true, true)
            .IsSuccess.Should().BeTrue();
    }

    [Fact]
    public void A_default_fills_a_field_the_creation_did_not_send_and_not_one_it_cleared()
    {
        var form = Form(Field("segment", FormFieldType.SingleChoice, false, "A", "B") with { DefaultValue = "A" });

        Stored(CustomFieldValues.Apply(form, null, null, NoNatives, false, true).Value)["segment"]!
            .GetValue<string>().Should().Be("A");

        CustomFieldValues.Apply(form, null, Values(new { segment = (string?)null }), NoNatives, false, true)
            .Value.Should().BeNull();
    }

    [Fact]
    public void A_tenant_required_native_field_is_reported_missing_but_an_entity_required_one_is_left_to_its_module()
    {
        var form = new EntityFormDto(FormEntity.Lead, 1, [],
        [
            new FormField("email", FormFieldType.Email, "E-mail", 1, IsRequired: true, QuickAdd: true, IsNative: true),
            new FormField("firstName", FormFieldType.Text, "Prénom", 2, IsRequired: true, QuickAdd: true,
                IsNative: true, IsRequiredLocked: true),
        ]);

        CustomFieldValues.MissingRequiredNatives(form, new Dictionary<string, string?> { ["email"] = " " })
            .Should().Equal("email");
    }
}
