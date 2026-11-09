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
}
