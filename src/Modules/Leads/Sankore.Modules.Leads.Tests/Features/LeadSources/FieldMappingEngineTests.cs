namespace Sankore.Modules.Leads.Tests.Features.LeadSources;

using FluentAssertions;
using Sankore.Modules.Leads.Features.LeadSources.Mapping;
using Xunit;

public sealed class FieldMappingEngineTests
{
    // ── Validate ──────────────────────────────────────────────────────────

    [Fact]
    public void Validate_accepts_valid_mapping()
    {
        var mapping = new Dictionary<string, string>
        {
            ["$.contact.phone"] = "phoneNumber|trim|e164:CI",
            ["$.contact.name"]  = "fullName|trim",
            ["$.contact.email"] = "email|trim"
        };

        var errors = FieldMappingEngine.Validate(mapping);
        errors.Should().BeEmpty();
    }

    [Fact]
    public void Validate_detects_missing_required_fields()
    {
        var mapping = new Dictionary<string, string>
        {
            ["$.email"] = "email"
        };

        var errors = FieldMappingEngine.Validate(mapping);
        errors.Should().Contain(e => e.Path.Contains("phoneNumber"));
        errors.Should().Contain(e => e.Path.Contains("fullName"));
    }

    [Fact]
    public void Validate_detects_unknown_target_field()
    {
        var mapping = new Dictionary<string, string>
        {
            ["$.phone"]   = "phoneNumber",
            ["$.name"]    = "fullName",
            ["$.unknown"] = "nonExistentField"
        };

        var errors = FieldMappingEngine.Validate(mapping);
        errors.Should().ContainSingle(e => e.Path == "$.unknown");
    }

    [Fact]
    public void Validate_detects_invalid_transform()
    {
        var mapping = new Dictionary<string, string>
        {
            ["$.phone"] = "phoneNumber|invalidTransform",
            ["$.name"]  = "fullName"
        };

        var errors = FieldMappingEngine.Validate(mapping);
        errors.Should().ContainSingle(e => e.Message.Contains("invalidTransform"));
    }

    [Fact]
    public void Validate_accepts_e164_transform_with_iso2()
    {
        var mapping = new Dictionary<string, string>
        {
            ["$.phone"] = "phoneNumber|e164:SN",
            ["$.name"]  = "fullName"
        };

        var errors = FieldMappingEngine.Validate(mapping);
        errors.Should().BeEmpty();
    }

    // ── Apply ─────────────────────────────────────────────────────────────

    [Fact]
    public void Apply_extracts_values_via_jsonpath()
    {
        var mapping = new Dictionary<string, string>
        {
            ["$.contact.firstName"] = "firstName|trim",
            ["$.contact.lastName"]  = "lastName|trim",
            ["$.contact.phone"]     = "phoneNumber|trim",
            ["$.contact.email"]     = "email"
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

        var result = FieldMappingEngine.Apply(mapping, payload);

        result.Errors.Should().BeEmpty();
        result.MappedValues["firstName"].Should().Be("Amadou");
        result.MappedValues["lastName"].Should().Be("Diallo");
        result.MappedValues["phoneNumber"].Should().Be("0707070707");
        result.MappedValues["email"].Should().Be("amadou@test.com");
    }

    [Fact]
    public void Apply_with_e164_normalizes_phone_number()
    {
        var mapping = new Dictionary<string, string>
        {
            ["$.phone"] = "phoneNumber|e164:CI",
            ["$.name"]  = "fullName"
        };

        var payload = """{"phone": "0707070707", "name": "Test"}""";

        var result = FieldMappingEngine.Apply(mapping, payload);

        result.Errors.Should().BeEmpty();
        result.MappedValues["phoneNumber"].Should().Be("+225707070707");
    }

    [Fact]
    public void Apply_with_invalid_phone_produces_error()
    {
        var mapping = new Dictionary<string, string>
        {
            ["$.phone"] = "phoneNumber|e164:CI",
            ["$.name"]  = "fullName"
        };

        var payload = """{"phone": "12", "name": "Test"}""";

        var result = FieldMappingEngine.Apply(mapping, payload);

        result.Errors.Should().ContainSingle(e =>
            e.Message.Contains("InvalidPhone"));
    }

    [Fact]
    public void Apply_missing_field_leaves_value_unset()
    {
        var mapping = new Dictionary<string, string>
        {
            ["$.phone"]   = "phoneNumber",
            ["$.name"]    = "fullName",
            ["$.missing"] = "email"
        };

        var payload = """{"phone": "0707070707", "name": "Test"}""";

        var result = FieldMappingEngine.Apply(mapping, payload);

        result.Errors.Should().BeEmpty();
        result.MappedValues.Should().NotContainKey("email");
    }

    [Fact]
    public void Apply_invalid_json_produces_root_error()
    {
        var mapping = new Dictionary<string, string>
        {
            ["$.phone"] = "phoneNumber"
        };

        var result = FieldMappingEngine.Apply(mapping, "not valid json");

        result.Errors.Should().ContainSingle(e => e.JsonPath == "(root)");
    }

    // ── NormalizeE164 ─────────────────────────────────────────────────────

    [Theory]
    [InlineData("+2250707070707", "CI", "+2250707070707")] // already E.164
    [InlineData("0707070707", "CI", "+225707070707")]       // local format CI
    [InlineData("0770001122", "SN", "+221770001122")]       // local format SN
    [InlineData("0612345678", "FR", "+33612345678")]         // local format FR
    public void NormalizeE164_handles_various_formats(string input, string iso2, string expected)
    {
        var (phone, error) = FieldMappingEngine.NormalizeE164(input, iso2);
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
