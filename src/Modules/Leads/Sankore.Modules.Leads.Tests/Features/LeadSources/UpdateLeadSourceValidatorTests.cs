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

    /// <summary>
    /// The composed url is validated through a <c>[JsonIgnore]</c> projection, so the C#
    /// expression (<c>Settings.EndpointUrl</c>) names nothing the caller sent.
    /// <c>ValidationExceptionHandler</c> keys the 422's <c>errors</c> dictionary on
    /// <c>PropertyName</c>, and <c>WithName</c> alone does not set it — the message read
    /// <c>settings.pull.baseUrl</c> while the key read <c>Settings.EndpointUrl</c>, which no
    /// front-end can attach to an input. <c>WithClientField</c> sets both.
    /// </summary>
    [Fact]
    public void Reports_a_malformed_pull_url_under_the_field_the_caller_sent()
    {
        var result = _validator.Validate(Command(PullSettings(baseUrl: "ftp://leads.example.com")));

        result.IsValid.Should().BeFalse();
        result.Errors.Should().ContainSingle()
            .Which.PropertyName.Should().Be("settings.pull.baseUrl");
    }

    /// <summary>
    /// A pull source is configured over several visits, so the save must not require the
    /// provider's URL. What refuses an incomplete one is <c>CheckActivationPrerequisites</c>
    /// (<c>NoEndpointConfigured</c>), and <c>LeadSourceConfig.UpdateSettings</c> refuses to blank
    /// the URL of a source already past Testing — see <c>LeadSourceConfigTests</c>.
    /// </summary>
    [Fact]
    public void Accepts_a_pull_source_whose_url_has_not_been_filled_in_yet()
    {
        _validator.Validate(Command(PullSettings(baseUrl: string.Empty))).IsValid.Should().BeTrue();
    }

    [Fact]
    public void Accepts_a_pull_url_composed_from_base_and_path()
    {
        var settings = PullSettings(baseUrl: "https://leads.example.com/") with
        {
            Pull = new PullConfig { BaseUrl = "https://leads.example.com/", RequestPath = "/v1/leads" }
        };

        _validator.Validate(Command(settings)).IsValid.Should().BeTrue();
    }

    private static ScheduledPullSettings PullSettings(string baseUrl) => new()
    {
        Pull = new PullConfig { BaseUrl = baseUrl },
        FieldMappings =
        [
            new() { SourceField = "$.data.phoneNumber", TargetField = "phoneNumber", Transformation = FieldTransformation.E164, E164Country = "+225" },
            new() { SourceField = "$.data.firstName", TargetField = "firstName" },
            new() { SourceField = "$.data.lastName", TargetField = "lastName" }
        ]
    };
}
