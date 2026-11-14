namespace Sankore.Modules.Leads.Tests.Features.Ingestion;

using FluentAssertions;
using Sankore.Modules.Leads.Features.Ingestion;
using Xunit;

/// <summary>Hosted forms saved before 2026-11-14 posted names the ingestion never read.</summary>
public sealed class LegacyFormFieldNamesTests
{
    [Fact]
    public void Old_hosted_form_names_reach_the_lead()
    {
        var fields = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            ["productCode"] = "Crédit", ["message"] = "Rappelez-moi", ["city"] = "Abidjan",
        };

        LegacyFormFieldNames.Apply(fields);

        fields["interestedProduct"].Should().Be("Crédit");
        fields["comment"].Should().Be("Rappelez-moi\nVille : Abidjan");
    }

    [Fact]
    public void The_crm_names_win_over_the_old_ones()
    {
        var fields = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            ["interestedProduct"] = "Épargne", ["productCode"] = "Crédit", ["comment"] = "Bonjour", ["message"] = "ignoré",
        };

        LegacyFormFieldNames.Apply(fields);

        fields["interestedProduct"].Should().Be("Épargne");
        fields["comment"].Should().Be("Bonjour");
    }

    [Fact]
    public void Nothing_is_added_when_no_old_name_is_sent()
    {
        var fields = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase) { ["phoneNumber"] = "0700000000" };

        LegacyFormFieldNames.Apply(fields);

        fields.Should().ContainSingle();
    }
}
