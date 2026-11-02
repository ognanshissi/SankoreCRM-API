namespace Sankore.Modules.Leads.Tests.Features.CaptureLead;

using FluentAssertions;
using Sankore.Modules.Leads.Domain;
using Sankore.Modules.Leads.Features.Import;
using Sankore.Modules.Leads.Features.WhatsApp.Bot;
using Sankore.Modules.Leads.Tests.TestSupport;
using Xunit;

/// <summary>
/// The profession, the marital status and the address reach the lead from every source the form
/// does (product owner, 2026-11-02): a file import, the WhatsApp bot, and a merge.
/// </summary>
public sealed class LeadSituationAndAddressSourcesTests
{
    private static ImportLeadRow Row() => new()
    {
        FullName          = "Awa Ndiaye",
        PhoneNumber       = "+221771234567",
        InterestedProduct = "Crédit individuel",
    };

    [Theory]
    [InlineData("Married", LeadMaritalStatus.Married)]
    [InlineData("Marié(e)", LeadMaritalStatus.Married)]
    [InlineData("célibataire", LeadMaritalStatus.Single)]
    [InlineData("Veuve", LeadMaritalStatus.Widowed)]
    [InlineData("Union libre", LeadMaritalStatus.FreeUnion)]
    public void An_import_reads_the_marital_status_by_name_or_by_label(string cell, LeadMaritalStatus expected)
    {
        var result = LeadRowParser.Parse(Row() with { MaritalStatus = cell }, new ImportDefaults(), LeadSource.FileImport);

        result.IsValid.Should().BeTrue(string.Join("; ", result.Errors));
        result.Row!.MaritalStatus.Should().Be(expected);
    }

    [Fact]
    public void An_import_carries_the_address_and_fails_the_row_on_an_unknown_country()
    {
        var ok = LeadRowParser.Parse(
            Row() with { Profession = " Couturière ", AddressLine = "Lot 12", PostalCode = "BP 45", City = "Dakar", CountryCode = "sn" },
            new ImportDefaults(), LeadSource.FileImport);

        ok.IsValid.Should().BeTrue();
        ok.Row!.Profession.Should().Be("Couturière");
        ok.Row.City.Should().Be("Dakar");
        ok.Row.CountryCode.Should().Be("SN");

        var bad = LeadRowParser.Parse(Row() with { CountryCode = "Sénégal", MaritalStatus = "Fiancé" }, new ImportDefaults(), LeadSource.FileImport);

        bad.IsValid.Should().BeFalse();
        bad.Errors.Should().HaveCount(2);
    }

    [Theory]
    [InlineData("Côte d'Ivoire", "CI")]
    [InlineData("burkina", "BF")]
    [InlineData("ml", "ML")]
    [InlineData("Atlantide", null)]
    public void The_bot_recognises_a_country_by_its_french_name_or_its_code(string text, string? expected)
        => WhatsAppBotFieldValues.CountryCodeOf(text).Should().Be(expected);

    [Theory]
    [InlineData("je suis mariée", null)]
    [InlineData("Mariée", LeadMaritalStatus.Married)]
    [InlineData("concubinage", LeadMaritalStatus.FreeUnion)]
    public void The_bot_keeps_a_marital_status_only_when_it_is_one_word_it_knows(string text, LeadMaritalStatus? expected)
        => WhatsAppBotFieldValues.MaritalStatusOf(text).Should().Be(expected);

    [Fact]
    public void A_merge_takes_the_source_address_as_a_block()
    {
        var source = LeadTestBuilder.Create().Build();
        source.EnrichPrincipalIdentity(null, null, null, null, null, "Rue 1", null, "Bouaké", "CI", DateTimeOffset.UtcNow);
        source.EnrichSituation("Enseignant", "Married", DateTimeOffset.UtcNow);

        var target = LeadTestBuilder.Create().Build();
        target.EnrichPrincipalIdentity(null, null, null, null, null, null, "01 BP 1", "Abidjan", null, DateTimeOffset.UtcNow);

        target.ApplyMergeOverrides(profession: source.Profession, maritalStatus: source.MaritalStatus, addressOf: source);

        target.Profession.Should().Be("Enseignant");
        target.MaritalStatus.Should().Be(LeadMaritalStatus.Married);
        target.PrincipalAddressLine.Should().Be("Rue 1");
        target.PrincipalCity.Should().Be("Bouaké");
        target.PrincipalCountryCode.Should().Be("CI");
        target.PrincipalPostalCode.Should().BeNull("the address moves as a whole, not mixed with the target's");
    }
}
