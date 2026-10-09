namespace Sankore.Modules.Integration.Tests.Features.Mappings;

using FluentAssertions;
using Sankore.Modules.Integration.Domain;
using Sankore.Modules.Integration.Features.Mappings;
using Xunit;

public sealed class MappingDomainRouteTests
{
    [Theory]
    [InlineData("IdDocType", MappingDomain.IdDocType)]
    [InlineData("product", MappingDomain.Product)]
    [InlineData("AGENCY", MappingDomain.Agency)]
    [InlineData("Country", MappingDomain.Country)]
    [InlineData("gender", MappingDomain.Gender)]
    [InlineData("maritalstatus", MappingDomain.MaritalStatus)]
    [InlineData("Profession", MappingDomain.Profession)]
    [InlineData("  Sector  ", MappingDomain.Sector)]
    public void Parses_every_domain_by_name_whatever_the_case(string segment, MappingDomain expected)
    {
        MappingDomainRoute.TryParse(segment, out var parsed).Should().BeTrue();
        parsed.Should().Be(expected);
    }

    [Fact]
    public void Covers_the_eight_domains_of_the_specification()
    {
        // A ninth value added to MappingDomain must be deliberate: it needs a CRM code list and a
        // line in ContractCrmCodeCatalog, so this count failing is the reminder.
        Enum.GetValues<MappingDomain>().Should().HaveCount(8);

        foreach (var domain in Enum.GetValues<MappingDomain>())
            MappingDomainRoute.TryParse(domain.ToString(), out _).Should().BeTrue();
    }

    [Theory]
    [InlineData("3")]
    [InlineData("-1")]
    [InlineData("0")]
    public void Refuses_a_numeric_segment(string segment)
    {
        // Enum.TryParse would accept "3" and answer Country. Addressing a domain by its ordinal
        // means the URL changes meaning the day a value is inserted in the middle of the enum.
        MappingDomainRoute.TryParse(segment, out _).Should().BeFalse();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("Produit")]
    [InlineData("import")]
    public void Refuses_anything_else(string? segment)
    {
        MappingDomainRoute.TryParse(segment, out _).Should().BeFalse();
    }
}
