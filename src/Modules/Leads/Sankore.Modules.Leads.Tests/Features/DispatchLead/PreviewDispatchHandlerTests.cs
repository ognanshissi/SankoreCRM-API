namespace Sankore.Modules.Leads.Tests.Features.DispatchLead;

using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Sankore.Modules.Administration.PublicApi;
using Sankore.Modules.Leads.Domain;
using Sankore.Modules.Leads.Features.DispatchLead;
using Sankore.Modules.Leads.Features.DispatchLead.PreviewDispatch;
using Sankore.Modules.Leads.Features.DispatchLead.Strategies;
using Sankore.Modules.Leads.Infrastructure;
using Sankore.Modules.Leads.Tests.TestSupport;
using Sankore.Shared.Infrastructure.Messaging;
using Sankore.Shared.Kernel;
using Xunit;

/// <summary>
/// The preview exists because the reassignment screen needed the engine's ranking and could only
/// get it by POSTing a dispatch — which assigned the lead, started its SLA and notified an agent
/// as a side effect of opening a drawer.
///
/// Two properties matter here and neither is obvious from the code: the preview must change
/// nothing, and it must agree with what a dispatch would actually do. The second one is why the
/// duplicated filter chain is tolerable, so it is pinned rather than assumed.
/// </summary>
public sealed class PreviewDispatchHandlerTests : IDisposable
{
    private readonly Guid _tenantId = Guid.NewGuid();
    private readonly TestDbContextFactory _factory;

    public PreviewDispatchHandlerTests() => _factory = new TestDbContextFactory(_tenantId);

    public void Dispose() => _factory.Dispose();

    private static DispatchingStrategyFactory Strategies() => new(
        compatibilityScoring: new CompatibilityScoringStrategy(),
        roundRobin: new RoundRobinStrategy(),
        weightedRoundRobin: new WeightedRoundRobinStrategy(),
        stickyAssignment: new StickyAssignmentStrategy(new CompatibilityScoringStrategy()),
        cherryPicking: new CherryPickingStrategy(new CompatibilityScoringStrategy()));

    private PreviewDispatchHandler Preview(LeadsDbContext db, IAdministrationModule users) =>
        new(db, new FixedTenantContext(_tenantId), users, new CompatibilityScorer(),
            Strategies(), new DispatchingRuleResolver(db), new AgentCapacityService(db, null));

    private DispatchLeadHandler Dispatch(
        LeadsDbContext db, IAdministrationModule users, IEventPublisher publisher) =>
        new(db, users, new CompatibilityScorer(), Strategies(),
            new DispatchingRuleResolver(db), new AgentCapacityService(db, null),
            publisher, NullLogger<DispatchLeadHandler>.Instance, TimeProvider.System);

    private static IAdministrationModule Users(params AgentSummary[] agents)
    {
        var module = Substitute.For<IAdministrationModule>();
        module.GetAvailableAgentsAsync(Arg.Any<Guid>(), Arg.Any<Guid?>(), Arg.Any<CancellationToken>())
            .Returns(agents.ToList());
        return module;
    }

    private async Task<Lead> SeedLeadAsync(LeadsDbContext db)
    {
        var lead = LeadTestBuilder.Create().WithTenant(_tenantId).WithLanguage("FR").Build();
        db.Leads.Add(lead);
        await db.SaveChangesAsync();
        return lead;
    }

    [Fact]
    public async Task Predicts_the_agent_a_real_dispatch_then_picks()
    {
        // The property that justifies duplicating the filter chain: preview and dispatch must
        // land on the same agent. If one drifts, this is what fails.
        await using var db = _factory.CreateContext();
        var lead = await SeedLeadAsync(db);

        var strong = AgentTestBuilder.Create()
            .Named("Awa Fall").SpeakingLanguages("FR").SpecializedIn("Crédit individuel")
            .LocatedAt(5.33, -4.03).WithLoad(1).WithConversionRate(0.9).Build();
        var weak = AgentTestBuilder.Create()
            .Named("Ibrahim Keita").SpeakingLanguages("EN")
            .LocatedAt(9.0, -1.0).WithLoad(20).WithConversionRate(0.1).Build();

        var users = Users(weak, strong);

        var preview = await Preview(db, users).Handle(
            new PreviewDispatchQuery(lead.Id), CancellationToken.None);

        preview.IsSuccess.Should().BeTrue();
        preview.Value.WouldAssignToAgentId.Should().NotBeNull();

        var dispatched = await Dispatch(db, users, Substitute.For<IEventPublisher>()).Handle(
            new DispatchLeadCommand(lead.Id, _tenantId), CancellationToken.None);

        dispatched.IsSuccess.Should().BeTrue();
        dispatched.Value.AgentId.Should().Be(
            preview.Value.WouldAssignToAgentId!.Value,
            "a preview that disagrees with the dispatch it previews is worse than no preview");
    }

    [Fact]
    public async Task Changes_nothing_and_publishes_nothing()
    {
        await using var db = _factory.CreateContext();
        var lead = await SeedLeadAsync(db);

        var publisher = Substitute.For<IEventPublisher>();
        var users = Users(AgentTestBuilder.Create().Named("Awa Fall")
            .SpeakingLanguages("FR").LocatedAt(5.33, -4.03).WithLoad(1).Build());

        var result = await Preview(db, users).Handle(
            new PreviewDispatchQuery(lead.Id), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();

        await using var verify = _factory.CreateContext();
        verify.LeadAssignments.Should().BeEmpty("a preview must not assign");
        verify.Leads.Single(l => l.Id == lead.Id).CurrentAssignmentId.Should().BeNull();
        verify.Set<Sankore.Shared.Infrastructure.Outbox.OutboxMessage>()
            .Should().BeEmpty("a preview must not publish — no SLA alert, no notification");
    }

    [Fact]
    public async Task Keeps_blocked_candidates_and_says_what_blocks_each()
    {
        // The dispatcher drops these; the screen needs them, greyed out with a reason. A list
        // that silently omits an agent cannot answer "why isn't X proposed?".
        await using var db = _factory.CreateContext();
        var lead = await SeedLeadAsync(db);

        var excluded = AgentTestBuilder.Create().Named("Agent Exclu")
            .SpeakingLanguages("FR").LocatedAt(5.33, -4.03).WithLoad(1).Build();
        var hoarder = AgentTestBuilder.Create().Named("Agent Monopole")
            .SpeakingLanguages("FR").LocatedAt(5.33, -4.03).WithLoad(1).Build()
            with { HotLeadsCount = 99 };
        var fine = AgentTestBuilder.Create().Named("Agent OK")
            .SpeakingLanguages("FR").LocatedAt(5.33, -4.03).WithLoad(1).Build();

        var rule = DispatchingRule.Create(
            _tenantId, "Règle du test", DispatchingStrategy.CompatibilityScoring,
            new ScoringWeights(Language: 25, Product: 25, Geography: 20, Workload: 15, Performance: 15),
            maxLeadsPerAgent: 30, antiMonopolyThreshold: 5,
            firstContactSla: TimeSpan.FromHours(2), priority: 10,
            excludedAgentIds: [excluded.Id]);
        db.DispatchingRules.Add(rule);
        await db.SaveChangesAsync();

        var result = await Preview(db, Users(excluded, hoarder, fine)).Handle(
            new PreviewDispatchQuery(lead.Id), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Candidates.Should().HaveCount(3, "blocked agents are reported, not hidden");

        var byId = result.Value.Candidates.ToDictionary(c => c.AgentId);
        byId[excluded.Id].IsExcludedByRule.Should().BeTrue();
        byId[excluded.Id].IsEligible.Should().BeFalse();
        byId[hoarder.Id].IsBlockedByAntiMonopoly.Should().BeTrue();
        byId[hoarder.Id].IsEligible.Should().BeFalse();
        byId[fine.Id].IsEligible.Should().BeTrue();

        result.Value.WouldAssignToAgentId.Should().Be(fine.Id);
        result.Value.Candidates[0].IsEligible.Should().BeTrue("eligible candidates sort first");
        result.Value.RuleName.Should().Be("Règle du test");
        result.Value.AntiMonopolyThreshold.Should().Be(5);
    }

    [Fact]
    public async Task Reports_a_real_score_not_an_estimate()
    {
        // The reason this endpoint exists: the front was approximating these numbers client-side.
        await using var db = _factory.CreateContext();
        var lead = await SeedLeadAsync(db);

        var match = AgentTestBuilder.Create()
            .Named("Awa Fall").SpeakingLanguages("FR").SpecializedIn("Crédit individuel")
            .LocatedAt(5.33, -4.03).WithLoad(1).WithConversionRate(0.9).Build();

        var result = await Preview(db, Users(match)).Handle(
            new PreviewDispatchQuery(lead.Id), CancellationToken.None);

        var candidate = result.Value.Candidates.Single();
        candidate.CompatibilityScore.Should().BeGreaterThan(0);
        candidate.CompatibilityFactorsJson.Should().NotBeNullOrWhiteSpace(
            "the per-factor breakdown is what lets the screen explain a score");
        candidate.FullName.Should().Be("Awa Fall");
    }

    [Fact]
    public async Task Says_nobody_would_be_assigned_rather_than_failing()
    {
        // Every candidate blocked is a legitimate answer, and the flags explain it. Failing here
        // would leave the screen unable to distinguish "no agents" from "all saturated".
        await using var db = _factory.CreateContext();
        var lead = await SeedLeadAsync(db);

        var hoarder = AgentTestBuilder.Create().Named("Agent Monopole")
            .SpeakingLanguages("FR").LocatedAt(5.33, -4.03).WithLoad(1).Build()
            with { HotLeadsCount = 99 };

        var rule = DispatchingRule.Create(
            _tenantId, "Anti-monopole serré", DispatchingStrategy.CompatibilityScoring,
            new ScoringWeights(Language: 25, Product: 25, Geography: 20, Workload: 15, Performance: 15),
            maxLeadsPerAgent: 30, antiMonopolyThreshold: 1,
            firstContactSla: TimeSpan.FromHours(2), priority: 10);
        db.DispatchingRules.Add(rule);
        await db.SaveChangesAsync();

        var result = await Preview(db, Users(hoarder)).Handle(
            new PreviewDispatchQuery(lead.Id), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.WouldAssignToAgentId.Should().BeNull();
        result.Value.Candidates.Should().ContainSingle(c => c.IsBlockedByAntiMonopoly);
    }

    [Fact]
    public async Task Refuses_a_closed_lead_and_an_unknown_one()
    {
        await using var db = _factory.CreateContext();

        var closed = LeadTestBuilder.Create().WithTenant(_tenantId).Build();
        closed.Close(LeadCloseReason.Lost);
        db.Leads.Add(closed);
        await db.SaveChangesAsync();

        var users = Users(AgentTestBuilder.Create().Named("Awa Fall")
            .SpeakingLanguages("FR").LocatedAt(5.33, -4.03).WithLoad(1).Build());

        var onClosed = await Preview(db, users).Handle(
            new PreviewDispatchQuery(closed.Id), CancellationToken.None);
        onClosed.IsFailure.Should().BeTrue();
        onClosed.Error.Should().Be("LEAD_NOT_DISPATCHABLE");

        var onMissing = await Preview(db, users).Handle(
            new PreviewDispatchQuery(Guid.NewGuid()), CancellationToken.None);
        onMissing.IsFailure.Should().BeTrue();
        onMissing.Error.Should().Be("LEAD_NOT_FOUND");
    }
}
