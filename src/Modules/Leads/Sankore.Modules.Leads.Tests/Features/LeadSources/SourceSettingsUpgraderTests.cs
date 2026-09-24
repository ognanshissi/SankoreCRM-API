namespace Sankore.Modules.Leads.Tests.Features.LeadSources;

using System.Text.Json;
using FluentAssertions;
using Sankore.Modules.Leads.Domain;
using Xunit;

/// <summary>
/// v1 stored the field mapping as a dictionary of "target|transform" expressions.
/// Rows written before the v2 rule model must still load with their mapping intact.
/// </summary>
public sealed class SourceSettingsUpgraderTests
{
    private static readonly JsonSerializerOptions Opts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
    };

    [Fact]
    public void MigrateJson_converts_v1_dictionary_into_rules()
    {
        const string v1 = """
        {
            "$mode": "ServerWebhook",
            "schemaVersion": 1,
            "contentType": "application/json",
            "fieldMapping": {
                "$.contact.phone": "phoneNumber|trim|e164:CI",
                "$.contact.name": "fullName|trim",
                "$.contact.mail": "email"
            }
        }
        """;

        var migrated = SourceSettingsUpgrader.MigrateJson(v1);
        var settings = JsonSerializer.Deserialize<ServerWebhookSettings>(migrated, Opts)!;

        settings.ContentType.Should().Be("application/json");
        settings.FieldMappings.Should().HaveCount(3);

        var phone = settings.FieldMappings!.Single(r => r.TargetField == "phoneNumber");
        phone.SourceField.Should().Be("$.contact.phone");
        phone.Transformation.Should().Be(FieldTransformation.E164);
        phone.E164Country.Should().Be("CI");

        var name = settings.FieldMappings!.Single(r => r.TargetField == "fullName");
        name.Transformation.Should().Be(FieldTransformation.Trim);

        var email = settings.FieldMappings!.Single(r => r.TargetField == "email");
        email.Transformation.Should().Be(FieldTransformation.None);

        migrated.Should().NotContain("\"fieldMapping\":");
    }

    [Fact]
    public void MigrateJson_leaves_v2_documents_untouched()
    {
        const string v2 = """
        {"$mode":"Internal","schemaVersion":2,"fieldMappings":[{"sourceField":"$.Phone","targetField":"phoneNumber","transformation":"E164","e164Country":"+225"}]}
        """;

        var migrated = SourceSettingsUpgrader.MigrateJson(v2);

        migrated.Should().Be(v2);
    }

    [Fact]
    public void MigrateJson_drops_legacy_key_when_rules_already_present()
    {
        const string mixed = """
        {"$mode":"ServerWebhook","fieldMapping":{"$.old":"email"},"fieldMappings":[{"sourceField":"$.new","targetField":"email"}]}
        """;

        var migrated = SourceSettingsUpgrader.MigrateJson(mixed);
        var settings = JsonSerializer.Deserialize<ServerWebhookSettings>(migrated, Opts)!;

        settings.FieldMappings.Should().ContainSingle()
            .Which.SourceField.Should().Be("$.new");
    }

    [Fact]
    public void MigrateJson_returns_malformed_input_unchanged()
    {
        SourceSettingsUpgrader.MigrateJson("not json").Should().Be("not json");
    }

    [Fact]
    public void Upgrade_stamps_current_schema_version()
    {
        var settings = new InternalSettings { SchemaVersion = 1 };

        var upgraded = SourceSettingsUpgrader.Upgrade(settings);

        upgraded.SchemaVersion.Should().Be(SourceSettingsUpgrader.CurrentVersion);
    }
}
