namespace Sankore.Modules.Leads.Tests.Features.Import;

using FluentAssertions;
using Sankore.Modules.Leads.Domain;
using Sankore.Modules.Leads.Features.Import;
using Xunit;

public sealed class LeadRowParserTests
{
    private static readonly ImportDefaults NoDefaults = new();

    private static ImportLeadRow ValidRow() => new()
    {
        FullName          = " Awa Ndiaye ",
        PhoneNumber       = "+221771234567",
        InterestedProduct = "Crédit individuel",
    };

    [Fact]
    public void A_minimal_row_parses_with_documented_fallbacks()
    {
        var result = LeadRowParser.Parse(ValidRow(), NoDefaults, LeadSource.FileImport);

        result.IsValid.Should().BeTrue();
        result.Row!.FullName.Should().Be("Awa Ndiaye");
        result.Row.PreferredLanguage.Should().Be("FR");
        result.Row.Source.Should().Be(LeadSource.FileImport);
        result.Row.Latitude.Should().Be(0);
        result.Row.Gender.Should().Be(LeadGender.Unknown);
    }

    [Fact]
    public void FullName_is_composed_from_first_and_last_when_missing()
    {
        var row = ValidRow() with { FullName = null, FirstName = "Awa", LastName = "Ndiaye" };

        LeadRowParser.Parse(row, NoDefaults, LeadSource.FileImport)
            .Row!.FullName.Should().Be("Awa Ndiaye");
    }

    [Fact]
    public void Job_defaults_fill_the_blanks_left_by_the_source()
    {
        // What a Google Contacts row looks like: a name and a phone, nothing else.
        var row = new ImportLeadRow { FullName = "Awa Ndiaye", PhoneNumber = "+221771234567" };
        var defaults = new ImportDefaults("Épargne", "WO", LeadSource.Referral);

        var result = LeadRowParser.Parse(row, defaults, LeadSource.FileImport);

        result.IsValid.Should().BeTrue();
        result.Row!.InterestedProduct.Should().Be("Épargne");
        result.Row.PreferredLanguage.Should().Be("WO");
        result.Row.Source.Should().Be(LeadSource.Referral);
    }

    [Fact]
    public void A_row_value_wins_over_the_job_default()
    {
        var row = ValidRow() with { Source = "whatsapp" };   // case-insensitive
        var defaults = new ImportDefaults(Source: LeadSource.Referral);

        LeadRowParser.Parse(row, defaults, LeadSource.FileImport)
            .Row!.Source.Should().Be(LeadSource.WhatsApp);
    }

    [Fact]
    public void Missing_required_fields_are_reported_together_not_thrown()
    {
        var result = LeadRowParser.Parse(new ImportLeadRow(), NoDefaults, LeadSource.FileImport);

        result.IsValid.Should().BeFalse();
        result.Row.Should().BeNull();
        result.Errors.Should().HaveCount(3);
        result.Errors.Should().Contain(e => e.Contains("FullName"));
        result.Errors.Should().Contain(e => e.Contains("PhoneNumber"));
        result.Errors.Should().Contain(e => e.Contains("InterestedProduct"));
    }

    [Theory]
    [InlineData("Latitude", "91", "out of range")]
    [InlineData("Latitude", "abc", "is not a number")]
    [InlineData("DateOfBirth", "31/02/2020", "not a valid date")]
    [InlineData("DesiredAmount", "1.000,50", "is not a number")]
    [InlineData("Source", "Carrier Pigeon", "not a known lead source")]
    [InlineData("Gender", "Yes", "is not valid")]
    [InlineData("OwnerId", "not-a-guid", "not a valid identifier")]
    public void A_bad_cell_fails_its_own_row_with_a_readable_message(
        string field, string value, string expected)
    {
        var row = ValidRow();
        row = field switch
        {
            "Latitude"      => row with { Latitude = value },
            "DateOfBirth"   => row with { DateOfBirth = value },
            "DesiredAmount" => row with { DesiredAmount = value, DesiredCurrency = "XOF" },
            "Source"        => row with { Source = value },
            "Gender"        => row with { Gender = value },
            "OwnerId"       => row with { OwnerId = value },
            _ => row
        };

        var result = LeadRowParser.Parse(row, NoDefaults, LeadSource.FileImport);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().ContainSingle().Which.Should().Contain(expected);
    }

    [Fact]
    public void An_amount_without_a_currency_is_rejected()
    {
        var row = ValidRow() with { DesiredAmount = "150000" };

        LeadRowParser.Parse(row, NoDefaults, LeadSource.FileImport)
            .Errors.Should().ContainSingle().Which.Should().Contain("DesiredCurrency is required");
    }

    [Fact]
    public void Optional_typed_fields_round_trip()
    {
        var row = ValidRow() with
        {
            Latitude      = "14.6928",
            Longitude     = "-17.4467",
            Gender        = "Female",
            DateOfBirth   = "1990-04-12",
            DesiredAmount = "150000.50",
            DesiredCurrency = "XOF",
            OwnerId       = "8f2b1c14-0c3a-4a2f-9f77-2f9a0b1d3e44",
        };

        var parsed = LeadRowParser.Parse(row, NoDefaults, LeadSource.FileImport);

        parsed.IsValid.Should().BeTrue();
        parsed.Row!.Latitude.Should().BeApproximately(14.6928, 0.0001);
        parsed.Row.Longitude.Should().BeApproximately(-17.4467, 0.0001);
        parsed.Row.Gender.Should().Be(LeadGender.Female);
        parsed.Row.DateOfBirth.Should().Be(new DateOnly(1990, 4, 12));
        parsed.Row.DesiredAmount.Should().Be(150000.50m);
        parsed.Row.OwnerId.Should().NotBeNull();
    }
}
