namespace Sankore.Modules.Leads.Tests.Features.DispatchLead;

using FluentAssertions;
using Sankore.Modules.Leads.Domain;
using Sankore.Modules.Leads.Features.DispatchLead;
using Sankore.Modules.Leads.Tests.TestSupport;
using Sankore.Shared.Kernel.ValueObject;
using Xunit;

/// <summary>
/// The rule used to be selected BY the strategy the caller named, so nothing could start from
/// the lead — and LeadSourceConfig.DefaultDispatchingRuleId, stored and exposed in the API since
/// F13.37, was read by no dispatching code at all.
/// </summary>
public sealed class DispatchingRuleResolverTests : IDisposable
{
    private readonly Guid _tenantId = Guid.NewGuid();
    private readonly TestDbContextFactory _factory;

    public DispatchingRuleResolverTests() => _factory = new TestDbContextFactory(_tenantId);

    public void Dispose() => _factory.Dispose();

    private Lead Fresh() => Lead.Capture(
        tenantId: _tenantId,
        fullName: "Awa Ouattara",
        phoneNumber: "+2250708091801",
        source: LeadSource.Web,
        interestedProduct: "Crédit commerçant",
        preferredLanguage: "FR",
        location: new GeoPoint(5.3, -4.0),
        preferredAgencyId: null,
        clock: TimeProvider.System);

    private DispatchingRule Rule(string name, DispatchingStrategy strategy, int priority) =>
        DispatchingRule.Create(
            _tenantId, name, strategy,
            new ScoringWeights(Language: 25, Product: 25, Geography: 20, Workload: 15, Performance: 15),
            maxLeadsPerAgent: 30, antiMonopolyThreshold: 5,
            firstContactSla: TimeSpan.FromHours(2), priority: priority);

    [Fact]
    public async Task Falls_back_to_the_built_in_defaults_when_no_rule_exists()
    {
        await using var db = _factory.CreateContext();

        var rule = await new DispatchingRuleResolver(db)
            .ResolveAsync(Fresh(), requestedStrategy: null, CancellationToken.None);

        rule.Id.Should().Be(Guid.Empty, "Default() is not a persisted rule");
        rule.Strategy.Should().Be(DispatchingStrategy.CompatibilityScoring);
    }

    [Fact]
    public async Task Picks_the_active_rule_with_the_highest_priority()
    {
        await using var db = _factory.CreateContext();
        var low = Rule("Basse", DispatchingStrategy.RoundRobin, priority: 1);
        var high = Rule("Haute", DispatchingStrategy.WeightedRoundRobin, priority: 9);
        db.DispatchingRules.AddRange(low, high);
        await db.SaveChangesAsync();

        var rule = await new DispatchingRuleResolver(db)
            .ResolveAsync(Fresh(), requestedStrategy: null, CancellationToken.None);

        rule.Id.Should().Be(high.Id);
        rule.Strategy.Should().Be(DispatchingStrategy.WeightedRoundRobin,
            "the rule carries the strategy — that is the whole inversion");
    }

    [Fact]
    public async Task An_explicitly_requested_strategy_still_wins()
    {
        // The manual dispatch screen must keep behaving exactly as before.
        await using var db = _factory.CreateContext();
        db.DispatchingRules.AddRange(
            Rule("Haute", DispatchingStrategy.WeightedRoundRobin, priority: 9),
            Rule("RoundRobin", DispatchingStrategy.RoundRobin, priority: 1));
        await db.SaveChangesAsync();

        var rule = await new DispatchingRuleResolver(db).ResolveAsync(
            Fresh(), requestedStrategy: DispatchingStrategy.RoundRobin, CancellationToken.None);

        rule.Strategy.Should().Be(DispatchingStrategy.RoundRobin);
        rule.Name.Should().Be("RoundRobin");
    }

    [Fact]
    public async Task The_rule_pinned_on_the_lead_source_beats_priority()
    {
        await using var db = _factory.CreateContext();

        var pinned = Rule("Épinglée", DispatchingStrategy.CherryPicking, priority: 0);
        var highest = Rule("Haute", DispatchingStrategy.WeightedRoundRobin, priority: 99);
        db.DispatchingRules.AddRange(pinned, highest);

        var source = LeadSourceConfig.Create(
            _tenantId, "WEB", "Formulaire web", LeadChannelType.WebForm, displayOrder: 0,
            defaultDispatchingRuleId: pinned.Id);
        db.LeadSourceConfigs.Add(source);

        var lead = Fresh();
        db.Leads.Add(lead);
        db.LeadIngestions.Add(LeadIngestion.Create(
            _tenantId, lead.Id, source.Id, TimeProvider.System));
        await db.SaveChangesAsync();

        var rule = await new DispatchingRuleResolver(db)
            .ResolveAsync(lead, requestedStrategy: null, CancellationToken.None);

        rule.Id.Should().Be(pinned.Id, "DefaultDispatchingRuleId is configuration, not decoration");
    }

    [Fact]
    public async Task A_lead_with_no_ingestion_row_falls_through_to_priority()
    {
        // File imports and UI-created leads have no ingestion row at all.
        await using var db = _factory.CreateContext();
        var highest = Rule("Haute", DispatchingStrategy.RoundRobin, priority: 5);
        db.DispatchingRules.Add(highest);
        await db.SaveChangesAsync();

        var rule = await new DispatchingRuleResolver(db)
            .ResolveAsync(Fresh(), requestedStrategy: null, CancellationToken.None);

        rule.Id.Should().Be(highest.Id);
    }

    [Fact]
    public async Task A_pinned_rule_that_was_deactivated_is_ignored()
    {
        await using var db = _factory.CreateContext();

        var pinned = Rule("Épinglée", DispatchingStrategy.CherryPicking, priority: 0);
        pinned.Deactivate();
        var active = Rule("Active", DispatchingStrategy.RoundRobin, priority: 1);
        db.DispatchingRules.AddRange(pinned, active);

        var source = LeadSourceConfig.Create(
            _tenantId, "WEB2", "Formulaire web", LeadChannelType.WebForm, displayOrder: 0,
            defaultDispatchingRuleId: pinned.Id);
        db.LeadSourceConfigs.Add(source);

        var lead = Fresh();
        db.Leads.Add(lead);
        db.LeadIngestions.Add(LeadIngestion.Create(
            _tenantId, lead.Id, source.Id, TimeProvider.System));
        await db.SaveChangesAsync();

        var rule = await new DispatchingRuleResolver(db)
            .ResolveAsync(lead, requestedStrategy: null, CancellationToken.None);

        rule.Id.Should().Be(active.Id);
    }
}
