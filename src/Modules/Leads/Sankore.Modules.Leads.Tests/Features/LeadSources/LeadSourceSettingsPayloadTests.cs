namespace Sankore.Modules.Leads.Tests.Features.LeadSources;

using System.Text.Json;
using System.Text.Json.Serialization;
using FluentAssertions;
using Sankore.Modules.Leads.Domain;
using Sankore.Modules.Leads.Features.LeadSources.Mapping;
using Sankore.Modules.Leads.Infrastructure.Configurations;
using Xunit;

/// <summary>
/// Pins the wire contract the UI posts to PUT /lead-sources/{id}.
/// Mirrors the options used by SourceSettingsJsonConverter in the API host.
/// </summary>
public sealed class LeadSourceSettingsPayloadTests
{
    private static readonly JsonSerializerOptions ApiOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private const string SettingsPayload = """
    {
        "$mode": "Internal",
        "ExpectedMode": "Internal",
        "RequireImmediateQualification": false,
        "DefaultPriority": null,
        "SchemaVersion": 1,
        "fieldMappings": [
            {"sourceField":"Phone","targetField":"phoneNumber","transformation":"e164","defaultValue":null,"e164Country":"+225","mapEntries":null,"concatSeparator":null},
            {"sourceField":"FirstName","targetField":"firstName","transformation":"trim","defaultValue":null,"e164Country":null,"mapEntries":null,"concatSeparator":null},
            {"sourceField":"LastName","targetField":"lastName","transformation":"none","defaultValue":null,"e164Country":null,"mapEntries":null,"concatSeparator":null}
        ]
    }
    """;

    [Fact]
    public void Ui_payload_binds_to_internal_settings()
    {
        var settings = JsonSerializer.Deserialize<InternalSettings>(SettingsPayload, ApiOpts)!;

        settings.FieldMappings.Should().HaveCount(3);

        var phone = settings.FieldMappings!.Single(r => r.TargetField == "phoneNumber");
        phone.SourceField.Should().Be("Phone");
        phone.Transformation.Should().Be(FieldTransformation.E164);
        phone.E164Country.Should().Be("+225");

        settings.FieldMappings!.Single(r => r.TargetField == "firstName")
            .Transformation.Should().Be(FieldTransformation.Trim);
        settings.FieldMappings!.Single(r => r.TargetField == "lastName")
            .Transformation.Should().Be(FieldTransformation.None);
    }

    [Fact]
    public void Ui_payload_passes_mapping_validation()
    {
        var settings = JsonSerializer.Deserialize<InternalSettings>(SettingsPayload, ApiOpts)!;

        FieldMappingEngine.Validate(settings.FieldMappings!).Should().BeEmpty();
    }

    [Fact]
    public void Ui_payload_maps_a_referral_lead()
    {
        var settings = JsonSerializer.Deserialize<InternalSettings>(SettingsPayload, ApiOpts)!;

        var result = FieldMappingEngine.Apply(
            settings.FieldMappings!,
            """{"Phone": "07 07 07 07 07", "FirstName": "  Amadou ", "LastName": "Diallo"}""");

        result.Errors.Should().BeEmpty();
        result.MappedValues["phoneNumber"].Should().Be("+225707070707");
        result.MappedValues["firstName"].Should().Be("Amadou");
        result.MappedValues["lastName"].Should().Be("Diallo");
    }

    [Fact]
    public void Settings_with_mappings_survive_a_storage_round_trip()
    {
        var settings = JsonSerializer.Deserialize<InternalSettings>(SettingsPayload, ApiOpts)!;

        var source = LeadSourceConfig.Create(
            Guid.NewGuid(), "PARRAINAGE", "Parrainage",
            LeadChannelType.Referral, 0, IntegrationMode.Internal, settings: settings);

        // Through the EF JSONB converter and back
        var converter = new SourceSettingsConverter();
        var json = (string?)converter.ConvertToProvider(source.Settings);
        var stored = (SourceSettings?)converter.ConvertFromProvider(json);

        json.Should().Contain("\"transformation\":\"E164\"");
        stored.FieldMappingsOf().Should().HaveCount(3);
        stored.FieldMappingsOf()!.Single(r => r.TargetField == "phoneNumber")
            .Transformation.Should().Be(FieldTransformation.E164);
    }
}
