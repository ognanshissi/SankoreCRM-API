namespace Sankore.Modules.Leads.Tests.Features.LeadSources;

using FluentAssertions;
using Sankore.Modules.Leads.Domain;
using Sankore.Modules.Leads.Features.LeadSources.Mapping;
using Xunit;

public sealed class FieldMappingEngineTests
{
    private static FieldMappingRule Rule(
        string source,
        string target,
        FieldTransformation transformation = FieldTransformation.None,
        string? e164Country = null,
        string? defaultValue = null,
        IReadOnlyDictionary<string, string>? mapEntries = null,
        string? concatSeparator = null)
        => new()
        {
            SourceField     = source,
            TargetField     = target,
            Transformation  = transformation,
            E164Country     = e164Country,
            DefaultValue    = defaultValue,
            MapEntries      = mapEntries,
            ConcatSeparator = concatSeparator
        };

    // ── Validate ──────────────────────────────────────────────────────────

    [Fact]
    public void Validate_accepts_valid_rules()
    {
        var rules = new[]
        {
            Rule("$.contact.phone", "phoneNumber", FieldTransformation.E164, e164Country: "CI"),
            Rule("$.contact.name",  "fullName",    FieldTransformation.Trim),
            Rule("$.contact.email", "email")
        };

        FieldMappingEngine.Validate(rules).Should().BeEmpty();
    }

    [Fact]
    public void Validate_accepts_firstName_lastName_instead_of_fullName()
    {
        var rules = new[]
        {
            Rule("$.Phone",     "phoneNumber", FieldTransformation.E164, e164Country: "+225"),
            Rule("$.FirstName", "firstName",   FieldTransformation.Trim),
            Rule("$.LastName",  "lastName")
        };

        FieldMappingEngine.Validate(rules).Should().BeEmpty();
    }

    [Fact]
    public void Validate_detects_missing_required_fields()
    {
        var rules = new[] { Rule("$.email", "email") };

        var errors = FieldMappingEngine.Validate(rules);

        errors.Should().Contain(e => e.Path.Contains("phoneNumber"));
        errors.Should().Contain(e => e.Path.Contains("fullName"));
    }

    [Fact]
    public void Validate_rejects_empty_rule_set()
    {
        var errors = FieldMappingEngine.Validate([]);
        errors.Should().ContainSingle(e => e.Path == "(rules)");
    }

    [Fact]
    public void Validate_detects_unknown_target_field()
    {
        var rules = new[]
        {
            Rule("$.phone",   "phoneNumber"),
            Rule("$.name",    "fullName"),
            Rule("$.unknown", "notALeadField")
        };

        var errors = FieldMappingEngine.Validate(rules);

        errors.Should().ContainSingle(e => e.Path == "$.unknown");
    }

    [Fact]
    public void Validate_requires_e164_country_for_e164_transform()
    {
        var rules = new[]
        {
            Rule("$.phone", "phoneNumber", FieldTransformation.E164),
            Rule("$.name",  "fullName")
        };

        var errors = FieldMappingEngine.Validate(rules);

        errors.Should().ContainSingle(e => e.Message.Contains("e164Country"));
    }

    [Fact]
    public void Validate_requires_map_entries_for_map_transform()
    {
        var rules = new[]
        {
            Rule("$.phone",  "phoneNumber"),
            Rule("$.name",   "fullName"),
            Rule("$.gender", "gender", FieldTransformation.Map)
        };

        var errors = FieldMappingEngine.Validate(rules);

        errors.Should().ContainSingle(e => e.Message.Contains("mapEntries"));
    }

    [Fact]
    public void Validate_rejects_duplicate_target_without_concat()
    {
        var rules = new[]
        {
            Rule("$.phone", "phoneNumber"),
            Rule("$.a",     "fullName"),
            Rule("$.b",     "fullName")
        };

        var errors = FieldMappingEngine.Validate(rules);

        errors.Should().ContainSingle(e => e.Message.Contains("mapped 2 times"));
    }

    [Fact]
    public void Validate_allows_duplicate_target_when_concatenating()
    {
        var rules = new[]
        {
            Rule("$.phone", "phoneNumber"),
            Rule("$.first", "fullName", FieldTransformation.Concat),
            Rule("$.last",  "fullName", FieldTransformation.Concat)
        };

        FieldMappingEngine.Validate(rules).Should().BeEmpty();
    }

    // ── Apply ─────────────────────────────────────────────────────────────

    [Fact]
    public void Apply_extracts_values_via_jsonpath()
    {
        var rules = new[]
        {
            Rule("$.contact.firstName", "firstName",   FieldTransformation.Trim),
            Rule("$.contact.lastName",  "lastName",    FieldTransformation.Trim),
            Rule("$.contact.phone",     "phoneNumber", FieldTransformation.Trim),
            Rule("$.contact.email",     "email")
        };

        var payload = """
        {
            "contact": {
                "firstName": "  Amadou  ",
                "lastName": "Diallo",
                "phone": "0707070707",
                "email": "amadou@test.com"
            }
        }
        """;

        var result = FieldMappingEngine.Apply(rules, payload);

        result.Errors.Should().BeEmpty();
        result.MappedValues["firstName"].Should().Be("Amadou");
        result.MappedValues["lastName"].Should().Be("Diallo");
        result.MappedValues["phoneNumber"].Should().Be("0707070707");
        result.MappedValues["email"].Should().Be("amadou@test.com");
    }

    [Fact]
    public void Apply_with_e164_normalizes_phone_number()
    {
        var rules = new[]
        {
            Rule("$.phone", "phoneNumber", FieldTransformation.E164, e164Country: "CI"),
            Rule("$.name",  "fullName")
        };

        var result = FieldMappingEngine.Apply(rules, """{"phone": "0707070707", "name": "Test"}""");

        result.Errors.Should().BeEmpty();
        result.MappedValues["phoneNumber"].Should().Be("+225707070707");
    }

    [Fact]
    public void Apply_accepts_dial_prefix_as_e164_country()
    {
        var rules = new[]
        {
            Rule("$.Phone", "phoneNumber", FieldTransformation.E164, e164Country: "+225"),
            Rule("$.Name",  "fullName")
        };

        var result = FieldMappingEngine.Apply(rules, """{"Phone": "0707070707", "Name": "Test"}""");

        result.Errors.Should().BeEmpty();
        result.MappedValues["phoneNumber"].Should().Be("+225707070707");
    }

    [Fact]
    public void Apply_with_invalid_phone_produces_error()
    {
        var rules = new[]
        {
            Rule("$.phone", "phoneNumber", FieldTransformation.E164, e164Country: "CI"),
            Rule("$.name",  "fullName")
        };

        var result = FieldMappingEngine.Apply(rules, """{"phone": "12", "name": "Test"}""");

        result.Errors.Should().ContainSingle(e => e.Message.Contains("InvalidPhone"));
    }

    [Fact]
    public void Apply_concatenates_rules_sharing_a_target()
    {
        var rules = new[]
        {
            Rule("$.phone", "phoneNumber"),
            Rule("$.first", "fullName", FieldTransformation.Concat),
            Rule("$.last",  "fullName", FieldTransformation.Concat)
        };

        var result = FieldMappingEngine.Apply(
            rules, """{"phone": "+2250707070707", "first": "Amadou", "last": "Diallo"}""");

        result.Errors.Should().BeEmpty();
        result.MappedValues["fullName"].Should().Be("Amadou Diallo");
    }

    [Fact]
    public void Apply_honours_custom_concat_separator()
    {
        var rules = new[]
        {
            Rule("$.last",  "fullName", FieldTransformation.Concat),
            Rule("$.first", "fullName", FieldTransformation.Concat, concatSeparator: ", ")
        };

        var result = FieldMappingEngine.Apply(rules, """{"first": "Amadou", "last": "Diallo"}""");

        result.MappedValues["fullName"].Should().Be("Diallo, Amadou");
    }

    [Fact]
    public void Apply_uses_default_value_when_source_is_missing()
    {
        var rules = new[]
        {
            Rule("$.phone", "phoneNumber"),
            Rule("$.name",  "fullName"),
            Rule("$.lang",  "preferredLanguage", defaultValue: "fr")
        };

        var result = FieldMappingEngine.Apply(rules, """{"phone": "+2250707070707", "name": "Test"}""");

        result.MappedValues["preferredLanguage"].Should().Be("fr");
    }

    [Fact]
    public void Apply_translates_values_through_map_entries()
    {
        var rules = new[]
        {
            Rule("$.sexe", "gender", FieldTransformation.Map,
                 mapEntries: new Dictionary<string, string> { ["H"] = "Male", ["F"] = "Female" })
        };

        var result = FieldMappingEngine.Apply(rules, """{"sexe": "h"}""");

        result.MappedValues["gender"].Should().Be("Male");
    }

    [Fact]
    public void Apply_missing_field_leaves_value_unset()
    {
        var rules = new[]
        {
            Rule("$.phone",   "phoneNumber"),
            Rule("$.name",    "fullName"),
            Rule("$.missing", "email")
        };

        var result = FieldMappingEngine.Apply(rules, """{"phone": "0707070707", "name": "Test"}""");

        result.Errors.Should().BeEmpty();
        result.MappedValues.Should().NotContainKey("email");
    }

    [Fact]
    public void Apply_invalid_json_produces_root_error()
    {
        var rules = new[] { Rule("$.phone", "phoneNumber") };

        var result = FieldMappingEngine.Apply(rules, "not valid json");

        result.Errors.Should().ContainSingle(e => e.JsonPath == "(root)");
    }

    // ── NormalizeE164 ─────────────────────────────────────────────────────

    [Theory]
    [InlineData("+2250707070707", "CI", "+2250707070707")] // already E.164
    [InlineData("0707070707", "CI", "+225707070707")]       // local format CI
    [InlineData("0770001122", "SN", "+221770001122")]       // local format SN
    [InlineData("0612345678", "FR", "+33612345678")]         // local format FR
    [InlineData("0707070707", "+225", "+225707070707")]      // dial prefix instead of ISO2
    [InlineData("0707070707", "225", "+225707070707")]       // bare dial prefix
    public void NormalizeE164_handles_various_formats(string input, string country, string expected)
    {
        var (phone, error) = FieldMappingEngine.NormalizeE164(input, country);
        error.Should().BeNull();
        phone.Should().Be(expected);
    }

    [Fact]
    public void NormalizeE164_rejects_too_short_number()
    {
        var (_, error) = FieldMappingEngine.NormalizeE164("12", "CI");
        error.Should().Contain("InvalidPhone");
    }

    [Fact]
    public void NormalizeE164_rejects_unknown_country()
    {
        var (_, error) = FieldMappingEngine.NormalizeE164("0707070707", "XX");
        error.Should().Contain("InvalidPhone");
        error.Should().Contain("XX");
    }
}
