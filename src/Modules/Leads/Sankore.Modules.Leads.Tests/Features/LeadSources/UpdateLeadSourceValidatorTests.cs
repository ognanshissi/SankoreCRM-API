namespace Sankore.Modules.Leads.Tests.Features.LeadSources;

using FluentAssertions;
using Sankore.Modules.Leads.Domain;
using Sankore.Modules.Leads.Features.LeadSources.UpdateLeadSource;
using Xunit;

public sealed class UpdateLeadSourceValidatorTests
{
    private readonly UpdateLeadSourceValidator _validator = new();

    private static UpdateLeadSourceCommand Command(SourceSettings? settings = null, int? displayOrder = null)
        => new(Guid.NewGuid(), 1, "Parrainage", displayOrder, Settings: settings);

    [Fact]
    public void Accepts_a_command_without_display_order()
    {
        _validator.Validate(Command()).IsValid.Should().BeTrue();
    }

    [Fact]
    public void Rejects_a_negative_display_order()
    {
        var result = _validator.Validate(Command(displayOrder: -1));

        result.IsValid.Should().BeFalse();
        result.Errors.Should().ContainSingle(e => e.PropertyName == nameof(UpdateLeadSourceCommand.DisplayOrder));
    }

    [Fact]
    public void Accepts_valid_field_mappings()
    {
        var settings = new InternalSettings
        {
            FieldMappings =
            [
                new() { SourceField = "Phone", TargetField = "phoneNumber", Transformation = FieldTransformation.E164, E164Country = "+225" },
                new() { SourceField = "FirstName", TargetField = "firstName", Transformation = FieldTransformation.Trim },
                new() { SourceField = "LastName", TargetField = "lastName" }
            ]
        };

        _validator.Validate(Command(settings)).IsValid.Should().BeTrue();
    }

    [Fact]
    public void Reports_one_failure_per_mapping_error()
    {
        // Unknown target, and neither fullName nor firstName+lastName mapped
        var settings = new InternalSettings
        {
            FieldMappings = [new() { SourceField = "$.x", TargetField = "notALeadField" }]
        };

        var result = _validator.Validate(Command(settings));

        result.IsValid.Should().BeFalse();
        result.Errors.Should().HaveCount(3);
        result.Errors.Should().OnlyContain(e => e.PropertyName == "settings.fieldMappings");
        result.Errors.Should().Contain(e => e.ErrorMessage.Contains("notALeadField"));
        result.Errors.Should().Contain(e => e.ErrorMessage.Contains("phoneNumber"));
        result.Errors.Should().Contain(e => e.ErrorMessage.Contains("fullName"));
    }

    [Fact]
    public void Ignores_settings_without_mappings()
    {
        _validator.Validate(Command(new EmbeddedScriptSettings())).IsValid.Should().BeTrue();
    }
}
