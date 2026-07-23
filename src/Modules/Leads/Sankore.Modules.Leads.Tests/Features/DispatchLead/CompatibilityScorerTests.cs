namespace Sankore.Modules.Leads.Tests.Features.DispatchLead;

using FluentAssertions;
using Sankore.Modules.Leads.Domain;
using Sankore.Modules.Leads.Features.DispatchLead;
using Sankore.Modules.Leads.Tests.TestSupport;
using Xunit;

public class CompatibilityScorerTests
{
    private readonly CompatibilityScorer _sut = new();

    [Fact]
    public void Score_should_be_high_when_language_product_and_location_all_match()
    {
        var lead = LeadTestBuilder.Create()
            .WithLanguage("Wolof")
            .WithProduct("Crédit individuel")
            .WithLocation(14.6928, -17.4467)
            .Build();

        var agent = AgentTestBuilder.Create()
            .SpeakingLanguages("Wolof", "FR")
            .SpecializedIn("Crédit individuel")
            .LocatedAt(14.6928, -17.4467)
            .WithLoad(2)
            .WithConversionRate(0.65)
            .Build();

        var score = _sut.Score(lead, agent, DispatchingRule.Default());

        score.Should().BeGreaterThan(85);
    }

    [Fact]
    public void Score_should_be_low_when_no_criteria_match()
    {
        var lead = LeadTestBuilder.Create()
            .WithLanguage("Bambara")
            .WithProduct("Épargne")
            .WithLocation(14.6928, -17.4467)
            .Build();

        var agent = AgentTestBuilder.Create()
            .SpeakingLanguages("EN")
            .SpecializedIn("Crédit groupe")
            .LocatedAt(20.0, -10.0)  // far away
            .WithLoad(50)             // fully loaded
            .WithConversionRate(0.0)
            .Build();

        var score = _sut.Score(lead, agent, DispatchingRule.Default());

        score.Should().BeLessOrEqualTo(5);
    }

    [Fact]
    public void Language_mismatch_alone_should_not_zero_out_the_whole_score()
    {
        // Even without a language match, a nearby specialized agent with low
        // load should still score meaningfully — no single criterion should
        // dominate absolutely under default weights.
        var lead = LeadTestBuilder.Create()
            .WithLanguage("Soninké")
            .WithProduct("Crédit individuel")
            .WithLocation(14.6928, -17.4467)
            .Build();

        var agent = AgentTestBuilder.Create()
            .SpeakingLanguages("FR")
            .SpecializedIn("Crédit individuel")
            .LocatedAt(14.70, -17.45) // ~1km away
            .WithLoad(0)
            .WithConversionRate(0.8)
            .Build();

        var score = _sut.Score(lead, agent, DispatchingRule.Default());

        score.Should().BeGreaterThan(50);
    }

    [Theory]
    [InlineData(0.5, 1.0)]    // 500m -> full proximity credit
    [InlineData(3.0, 0.8)]    // 3km  -> good credit
    [InlineData(8.0, 0.5)]    // 8km  -> medium credit
    [InlineData(15.0, 0.2)]   // 15km -> low credit
    [InlineData(50.0, 0.0)]   // 50km -> no credit
    public void Distance_decay_buckets_should_match_specification(double distanceKm, double expectedFactor)
    {
        // Arrange two points at approximately `distanceKm` apart on the same meridian.
        var lat2 = 14.6928 + (distanceKm / 111.0); // ~111km per degree latitude

        var lead = LeadTestBuilder.Create()
            .WithLanguage("__none__")
            .WithProduct("__none__")
            .WithLocation(14.6928, -17.4467)
            .Build();

        var agent = AgentTestBuilder.Create()
            .SpeakingLanguages("__other__")
            .SpecializedIn("__other__")
            .LocatedAt(lat2, -17.4467)
            .WithLoad(0)
            .WithConversionRate(0)
            .Build();

        var rules = DispatchingRule.Default();
        var score = _sut.Score(lead, agent, rules);

        // Only the geography weight contributes here (language/product/workload/perf all zero).
        var expectedScore = Math.Round(rules.Weights.Geography * expectedFactor, 2);
        score.Should().BeApproximately(expectedScore, 1.0);
    }
}
