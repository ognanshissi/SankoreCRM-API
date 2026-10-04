namespace Sankore.Modules.Leads.Tests.Features.DispatchLead;

using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Sankore.Modules.Administration.PublicApi;
using Sankore.Modules.Leads.Domain;
using Sankore.Modules.Leads.Features.DispatchLead;
using Sankore.Modules.Leads.Features.DispatchLead.Events;
using Sankore.Modules.Leads.Features.DispatchLead.Strategies;
using Sankore.Modules.Leads.Infrastructure;
using Sankore.Modules.Leads.Tests.TestSupport;
using Sankore.Shared.Infrastructure.Messaging;
using Xunit;

/// <summary>
/// The supervisor override: name the agent instead of ranking a pool.
///
/// It exists because no strategy can express "give this lead to that person" — every strategy
/// ranks candidates and takes the winner. Without it, someone who wanted a specific agent on a
/// lead could only set the lead's OWNER, which is a different field: it creates no assignment, so
/// RecordFirstContact kept refusing the lead and the SLA clock never started.
///
/// These tests pin which gates the override keeps and which it drops, because that split is the
/// whole design and it is not self-evident from the code.
/// </summary>
public sealed class DispatchLeadExplicitAgentTests : IDisposable
{
    private readonly Guid _tenantId = Guid.NewGuid();
    private readonly TestDbContextFactory _factory;

    public DispatchLeadExplicitAgentTests() => _factory = new TestDbContextFactory(_tenantId);

    public void Dispose() => _factory.Dispose();

    private static AgentSummary Agent(string name, int load = 1, int hot = 0) =>
        AgentTestBuilder.Create()
            .Named(name)
            .SpeakingLanguages("FR")
            .LocatedAt(5.33, -4.03)
            .WithLoad(load)
            .Build() with { HotLeadsCount = hot };

    private DispatchLeadHandler Handler(
        LeadsDbContext db, IAdministrationModule usersModule, IEventPublisher publisher)
    {
        var factory = new DispatchingStrategyFactory(
            compatibilityScoring: new CompatibilityScoringStrategy(),
            roundRobin: new RoundRobinStrategy(),
            weightedRoundRobin: new WeightedRoundRobinStrategy(),
            stickyAssignment: new StickyAssignmentStrategy(new CompatibilityScoringStrategy()),
            cherryPicking: new CherryPickingStrategy(new CompatibilityScoringStrategy()));

        return new DispatchLeadHandler(
            db, usersModule, new CompatibilityScorer(), factory,
            new DispatchingRuleResolver(db), new AgentCapacityService(db, null),
            publisher, NullLogger<DispatchLeadHandler>.Instance, TimeProvider.System);
    }

    private async Task<Lead> SeedLeadAsync(LeadsDbContext db)
    {
        var lead = LeadTestBuilder.Create().WithTenant(_tenantId).WithLanguage("FR").Build();
        db.Leads.Add(lead);
        await db.SaveChangesAsync();
        return lead;
    }

    [Fact]
    public async Task Assigns_the_named_agent_even_when_another_would_score_higher()
    {
        await using var db = _factory.CreateContext();
        var lead = await SeedLeadAsync(db);

        // The better match on paper, and the one the engine would pick unaided.
        var best = AgentTestBuilder.Create()
            .Named("Awa Fall").SpeakingLanguages("FR").SpecializedIn("Crédit individuel")
            .LocatedAt(5.33, -4.03).WithLoad(1).WithConversionRate(0.9).Build();
        var chosen = AgentTestBuilder.Create()
            .Named("Ibrahim Keita").SpeakingLanguages("EN")
            .LocatedAt(9.0, -1.0).WithLoad(20).WithConversionRate(0.1).Build();

        var usersModule = Substitute.For<IAdministrationModule>();
        usersModule.GetAvailableAgentsAsync(_tenantId, Arg.Any<Guid?>(), Arg.Any<CancellationToken>())
            .Returns(new List<AgentSummary> { best, chosen });

        var publisher = Substitute.For<IEventPublisher>();

        var result = await Handler(db, usersModule, publisher).Handle(
            new DispatchLeadCommand(
                lead.Id, _tenantId, Strategy: null,
                AgentId: chosen.Id, OverrideReason: "Client a demandé Ibrahim"),
            CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.AgentId.Should().Be(
            chosen.Id, "naming an agent must beat the ranking, that is the point of the override");
        result.Value.AgentName.Should().Be("Ibrahim Keita");

        await using var verify = _factory.CreateContext();
        var assignment = verify.LeadAssignments.Single(a => a.LeadId == lead.Id);
        assignment.AgentId.Should().Be(chosen.Id);
        assignment.WasManualOverride.Should().BeTrue("GetAssignmentHistory shows this flag");
        assignment.OverrideReason.Should().Be("Client a demandé Ibrahim");
        assignment.RuleId.Should().BeNull("no rule produced this assignment");

        verify.Leads.Single(l => l.Id == lead.Id).CurrentAssignmentId.Should().Be(assignment.Id);

        await publisher.Received(1).PublishAsync(
            Arg.Is<LeadDispatchedEvent>(e => e.LeadId == lead.Id && e.AgentId == chosen.Id),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Refuses_an_agent_the_users_module_does_not_offer()
    {
        // Covers unknown id, inactive account, wrong agency, not available — every "may not
        // receive this lead" case. The override skips the ranking, never the eligibility.
        await using var db = _factory.CreateContext();
        var lead = await SeedLeadAsync(db);

        var usersModule = Substitute.For<IAdministrationModule>();
        usersModule.GetAvailableAgentsAsync(_tenantId, Arg.Any<Guid?>(), Arg.Any<CancellationToken>())
            .Returns(new List<AgentSummary> { Agent("Awa Fall") });

        var publisher = Substitute.For<IEventPublisher>();

        var result = await Handler(db, usersModule, publisher).Handle(
            new DispatchLeadCommand(
                lead.Id, _tenantId, Strategy: null,
                AgentId: Guid.NewGuid(), OverrideReason: "au hasard"),
            CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Be("AGENT_NOT_ELIGIBLE");

        await using var verify = _factory.CreateContext();
        verify.LeadAssignments.Should().BeEmpty();
        verify.Leads.Single(l => l.Id == lead.Id).CurrentAssignmentId.Should().BeNull();
    }

    [Fact]
    public async Task Refuses_an_agent_on_the_rules_exclusion_list()
    {
        // An exclusion is an administrator saying "not this one". Naming the agent must not be a
        // way round it — unlike the load heuristics, which the override does bypass.
        await using var db = _factory.CreateContext();
        var lead = await SeedLeadAsync(db);
        var excluded = Agent("Agent Exclu");

        var rule = DispatchingRule.Create(
            _tenantId, "Avec exclusion", DispatchingStrategy.CompatibilityScoring,
            new ScoringWeights(Language: 25, Product: 25, Geography: 20, Workload: 15, Performance: 15),
            maxLeadsPerAgent: 30, antiMonopolyThreshold: 5,
            firstContactSla: TimeSpan.FromHours(2), priority: 10,
            excludedAgentIds: [excluded.Id]);
        db.DispatchingRules.Add(rule);
        await db.SaveChangesAsync();

        var usersModule = Substitute.For<IAdministrationModule>();
        usersModule.GetAvailableAgentsAsync(_tenantId, Arg.Any<Guid?>(), Arg.Any<CancellationToken>())
            .Returns(new List<AgentSummary> { excluded });

        var publisher = Substitute.For<IEventPublisher>();

        var result = await Handler(db, usersModule, publisher).Handle(
            new DispatchLeadCommand(
                lead.Id, _tenantId, Strategy: null,
                AgentId: excluded.Id, OverrideReason: "insistance du client"),
            CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Be("AGENT_EXCLUDED_BY_RULE");
    }

    [Fact]
    public async Task Assigns_past_the_anti_monopoly_threshold()
    {
        // Deliberate: anti-monopoly shapes how the ENGINE spreads leads. A supervisor who has
        // named a person cannot act on "ANTI_MONOPOLY_BLOCKED", and the override is recorded, so
        // the limit does not veto it. Flip this test if the product decides otherwise.
        await using var db = _factory.CreateContext();
        var lead = await SeedLeadAsync(db);

        var rule = DispatchingRule.Create(
            _tenantId, "Anti-monopole serré", DispatchingStrategy.CompatibilityScoring,
            new ScoringWeights(Language: 25, Product: 25, Geography: 20, Workload: 15, Performance: 15),
            maxLeadsPerAgent: 30, antiMonopolyThreshold: 1,
            firstContactSla: TimeSpan.FromHours(2), priority: 10);
        db.DispatchingRules.Add(rule);
        await db.SaveChangesAsync();

        var saturated = Agent("Agent Saturé", load: 25, hot: 99);

        var usersModule = Substitute.For<IAdministrationModule>();
        usersModule.GetAvailableAgentsAsync(_tenantId, Arg.Any<Guid?>(), Arg.Any<CancellationToken>())
            .Returns(new List<AgentSummary> { saturated });

        var publisher = Substitute.For<IEventPublisher>();

        var result = await Handler(db, usersModule, publisher).Handle(
            new DispatchLeadCommand(
                lead.Id, _tenantId, Strategy: null,
                AgentId: saturated.Id, OverrideReason: "reprise du portefeuille"),
            CancellationToken.None);

        result.IsSuccess.Should().BeTrue(
            "an explicit choice is not vetoed by a load-balancing threshold");
        result.Value.AgentId.Should().Be(saturated.Id);
    }

    [Fact]
    public async Task Still_refuses_a_lead_in_a_terminal_state()
    {
        await using var db = _factory.CreateContext();
        var lead = LeadTestBuilder.Create().WithTenant(_tenantId).Build();
        lead.Close(LeadCloseReason.Lost);
        db.Leads.Add(lead);
        await db.SaveChangesAsync();

        var agent = Agent("Awa Fall");
        var usersModule = Substitute.For<IAdministrationModule>();
        usersModule.GetAvailableAgentsAsync(_tenantId, Arg.Any<Guid?>(), Arg.Any<CancellationToken>())
            .Returns(new List<AgentSummary> { agent });

        var result = await Handler(db, usersModule, Substitute.For<IEventPublisher>()).Handle(
            new DispatchLeadCommand(
                lead.Id, _tenantId, Strategy: null,
                AgentId: agent.Id, OverrideReason: "peu importe"),
            CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Be("LEAD_NOT_DISPATCHABLE");
    }

    [Fact]
    public async Task The_automatic_path_is_unchanged_when_no_agent_is_named()
    {
        await using var db = _factory.CreateContext();
        var lead = await SeedLeadAsync(db);

        var best = AgentTestBuilder.Create()
            .Named("Awa Fall").SpeakingLanguages("FR")
            .LocatedAt(5.33, -4.03).WithLoad(1).WithConversionRate(0.9).Build();
        var worse = AgentTestBuilder.Create()
            .Named("Ibrahim Keita").SpeakingLanguages("EN")
            .LocatedAt(9.0, -1.0).WithLoad(20).WithConversionRate(0.1).Build();

        var usersModule = Substitute.For<IAdministrationModule>();
        usersModule.GetAvailableAgentsAsync(_tenantId, Arg.Any<Guid?>(), Arg.Any<CancellationToken>())
            .Returns(new List<AgentSummary> { worse, best });

        var result = await Handler(db, usersModule, Substitute.For<IEventPublisher>()).Handle(
            new DispatchLeadCommand(lead.Id, _tenantId), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.AgentId.Should().Be(best.Id, "the ranking still decides without an AgentId");

        await using var verify = _factory.CreateContext();
        verify.LeadAssignments.Single(a => a.LeadId == lead.Id)
            .WasManualOverride.Should().BeFalse();
    }
}
