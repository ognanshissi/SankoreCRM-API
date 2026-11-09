namespace Sankore.Modules.Leads.Tests.Features.StartingData;

using FluentAssertions;
using Sankore.Modules.Leads.Features.StartingData;
using Xunit;

/// <summary>
/// The demo's commercial activity (lot 4) shows every stage: what a presenter opens must not be
/// empty, and no prospect's number may collide with another's.
/// </summary>
public sealed class LeadsDemoDataShapeTests
{
    [Fact]
    public void Every_fate_is_represented()
    {
        var fates = Enumerable.Range(0, LeadsDemoData.LeadCount).Select(LeadsDemoData.FateOf).ToList();

        fates.Distinct().Should().BeEquivalentTo(Enum.GetValues<LeadsDemoData.Fate>());
        fates.Count(f => f == LeadsDemoData.Fate.Unassigned).Should().Be(6);
    }

    [Fact]
    public void Prospects_have_distinct_numbers_reserved_for_the_demo()
    {
        var phones = Enumerable.Range(1, LeadsDemoData.LeadCount).Select(LeadsDemoData.Phone).ToList();

        phones.Should().OnlyHaveUniqueItems().And.OnlyContain(p => p.StartsWith("+22507980") && p.Length == 14);
    }

    [Fact]
    public void Credit_files_cover_the_three_first_steps()
        => LeadsDemoData.CreditFiles.Select(f => f.Target).Distinct().Should().BeEquivalentTo([0, 1, 2]);

    [Fact]
    public void A_socle_demo_asks_for_standard_offers_never_a_credit()
    {
        // docs/isolation-socle.md, lot 5.
        var socle = LeadsDemoData.ProductsFor(banking: false, insurance: false);
        socle.Should().NotContain(p => p.Contains("Crédit") || p.Contains("Assurance") || p.Contains("Tontine") || p.Contains("épargne"));
        LeadsDemoData.DesiredAmount(socle[0], 0).Should().NotBeNull();

        LeadsDemoData.ProductsFor(banking: false, insurance: true).Should().BeEquivalentTo("Assurance santé", "Prévoyance décès");
        LeadsDemoData.ProductsFor(banking: true, insurance: false).Should().Contain("Crédit individuel").And.NotContain("Assurance santé");
    }

    [Fact]
    public void The_leads_that_open_a_project_do_not_all_ask_for_the_same_product()
    {
        var socle = LeadsDemoData.ProductsFor(banking: false, insurance: false);
        Enumerable.Range(0, LeadsDemoData.LeadCount)
            .Where(i => LeadsDemoData.FateOf(i) == LeadsDemoData.Fate.QualifiedWithProject)
            .Select(i => LeadsDemoData.ProductOf(socle, i))
            .Distinct().Should().HaveCountGreaterThan(1);
    }
}
