namespace Sankore.Modules.Leads.Tests.Features.DispatchLead;

using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Sankore.Modules.Administration.PublicApi;
using Sankore.Modules.Leads.Domain;
using Sankore.Modules.Leads.Features.DispatchLead;
using Sankore.Modules.Leads.Features.DispatchLead.Strategies;
using Sankore.Modules.Leads.Features.ListLeads;
using Sankore.Modules.Leads.Features.Settings.KeyAccounts;
using Sankore.Modules.Leads.Infrastructure;
using Sankore.Modules.Leads.Tests.TestSupport;
using Sankore.Shared.Infrastructure.Messaging;
using Sankore.Shared.Kernel;
using Sankore.Shared.Kernel.ValueObject;
using Xunit;

/// <summary>
/// The key accounts go to the right people (key accounts lot 4, 2026-11-09): a company prospect at the
/// « grand compte » threshold is dispatched by the key-account rule, before any other — a source's
/// pinned rule included —, that rule never receives another lead, and a rule's reserved agents are
/// the only ones it dispatches to. The same tier filters the list.
/// </summary>
public sealed class KeyAccountDispatchTests : IDisposable
{
    private readonly Guid _tenantId = Guid.NewGuid();
    private readonly TestDbContextFactory _factory;

    public KeyAccountDispatchTests() => _factory = new TestDbContextFactory(_tenantId);

    public void Dispose() => _factory.Dispose();

    private static DispatchingRule Rule(Guid tenantId, string name, int priority, bool keyAccounts = false, params Guid[] reserved)
        => DispatchingRule.Create(tenantId, name, DispatchingStrategy.RoundRobin,
            new ScoringWeights(25, 25, 20, 15, 15), 30, 5, TimeSpan.FromHours(24), priority,
            keyAccountsOnly: keyAccounts, reservedAgentIds: reserved);

    private Lead Company(decimal purchases)
    {
        var lead = LeadTestBuilder.Create().WithTenant(_tenantId).Build();
        lead.ChangeProspectType(LeadType.Corporate);
        lead.SetEstimatedAnnualPurchases(new Money(purchases, "XOF"));
        return lead;
    }

    private async Task ThresholdsAsync(LeadsDbContext db)
    {
        var result = await new KeyAccountSettingsHandler(new LeadSettingsService(db))
            .Handle(new SetKeyAccountSettingsCommand(_tenantId, 10_000_000m, 50_000_000m, [], Guid.NewGuid()), default);
        result.IsSuccess.Should().BeTrue(result.Error);
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task A_key_account_goes_to_the_key_account_rule_and_nothing_else_does()
    {
        await using var db = _factory.CreateContext();
        await ThresholdsAsync(db);
        var general = Rule(_tenantId, "Général", priority: 10);
        var keyRule = Rule(_tenantId, "Grands comptes", priority: 1, keyAccounts: true);
        db.DispatchingRules.AddRange(general, keyRule);
        await db.SaveChangesAsync();

        var resolver = new DispatchingRuleResolver(db);
        (await resolver.ResolveAsync(Company(80_000_000m), null, default)).Id.Should().Be(keyRule.Id);
        (await resolver.ResolveAsync(Company(20_000_000m), null, default)).Id.Should().Be(general.Id,
            "a good customer is not a key account");
        (await resolver.ResolveAsync(LeadTestBuilder.Create().WithTenant(_tenantId).Build(), null, default))
            .Id.Should().Be(general.Id);
    }

    [Fact]
    public async Task The_key_account_rule_wins_over_a_source_pinned_rule()
    {
        await using var db = _factory.CreateContext();
        await ThresholdsAsync(db);
        var pinnedRule = Rule(_tenantId, "Site web", priority: 10);
        var keyRule = Rule(_tenantId, "Grands comptes", priority: 0, keyAccounts: true);
        db.DispatchingRules.AddRange(pinnedRule, keyRule);
        var source = LeadSourceConfig.Create(_tenantId, "WEB", "Site", LeadChannelType.WebForm, 0,
            defaultDispatchingRuleId: pinnedRule.Id);
        db.LeadSourceConfigs.Add(source);
        await db.SaveChangesAsync();

        var keyAccount = LeadTestBuilder.Create().WithTenant(_tenantId).WithSourceConfig(source.Id).Build();
        keyAccount.ChangeProspectType(LeadType.Corporate);
        keyAccount.SetEstimatedAnnualPurchases(new Money(80_000_000m, "XOF"));
        var other = LeadTestBuilder.Create().WithTenant(_tenantId).WithSourceConfig(source.Id).Build();

        var resolver = new DispatchingRuleResolver(db);
        (await resolver.ResolveAsync(keyAccount, null, default)).Id.Should().Be(keyRule.Id);
        (await resolver.ResolveAsync(other, null, default)).Id.Should().Be(pinnedRule.Id);
    }

    [Fact]
    public async Task Without_thresholds_the_key_account_rule_receives_nobody()
    {
        await using var db = _factory.CreateContext();
        var keyRule = Rule(_tenantId, "Grands comptes", priority: 10, keyAccounts: true);
        db.DispatchingRules.Add(keyRule);
        await db.SaveChangesAsync();

        (await new DispatchingRuleResolver(db).ResolveAsync(Company(80_000_000m), null, default))
            .Id.Should().Be(Guid.Empty, "the built-in defaults, never the key-account rule");
    }

    [Fact]
    public async Task A_rule_dispatches_only_to_its_reserved_agents()
    {
        var senior = Guid.NewGuid();
        var junior = Guid.NewGuid();
        Lead lead;
        await using (var db = _factory.CreateContext())
        {
            db.DispatchingRules.Add(Rule(_tenantId, "Réservée", priority: 10, keyAccounts: false, senior));
            lead = LeadTestBuilder.Create().WithTenant(_tenantId).Build();
            db.Leads.Add(lead);
            await db.SaveChangesAsync();
        }

        var admin = Substitute.For<IAdministrationModule>();
        admin.GetAvailableAgentsAsync(_tenantId, Arg.Any<Guid?>(), Arg.Any<CancellationToken>())
            .Returns(new List<AgentSummary>
            {
                new(junior, "Agent junior", Guid.NewGuid(), ["fr"], [], null, 0, 0, 0, true),
                new(senior, "Commerciale senior", Guid.NewGuid(), ["fr"], [], null, 0, 0, 0, true),
            });

        await using var dispatchDb = _factory.CreateContext();
        var factory = new DispatchingStrategyFactory(
            compatibilityScoring: new CompatibilityScoringStrategy(),
            roundRobin: new RoundRobinStrategy(),
            weightedRoundRobin: new WeightedRoundRobinStrategy(),
            stickyAssignment: new StickyAssignmentStrategy(new CompatibilityScoringStrategy()),
            cherryPicking: new CherryPickingStrategy(new CompatibilityScoringStrategy()));
        var handler = new DispatchLeadHandler(
            dispatchDb, admin, new CompatibilityScorer(), factory, new DispatchingRuleResolver(dispatchDb),
            new AgentCapacityService(dispatchDb, null),
            TestClientAdvisorResolver.None(), TestDistributionGuard.AllowAll(),
            Substitute.For<IEventPublisher>(), NullLogger<DispatchLeadHandler>.Instance, TimeProvider.System);

        var result = await handler.Handle(new DispatchLeadCommand(lead.Id, _tenantId), default);

        result.IsSuccess.Should().BeTrue(result.Error);
        result.Value.AgentId.Should().Be(senior);
    }

    [Fact]
    public async Task The_list_filters_by_tier()
    {
        var major = Company(80_000_000m);
        var good = Company(20_000_000m);
        var small = Company(1_000_000m);
        var person = LeadTestBuilder.Create().WithTenant(_tenantId).Build();
        await using var db = _factory.CreateContext();
        await ThresholdsAsync(db);
        db.Leads.AddRange(major, good, small, person);
        await db.SaveChangesAsync();

        var tenant = Substitute.For<ITenantContext>();
        tenant.CurrentTenantId.Returns(_tenantId);

        async Task<List<Guid>> Ids(LeadPotentialTier tier)
        {
            var q = await new LeadListFilter(Potential: tier).ApplyAsync(db.Leads, db, null, tenant, default);
            return q.Value.Select(l => l.Id).ToList();
        }

        (await Ids(LeadPotentialTier.KeyAccount)).Should().Equal(major.Id);
        (await Ids(LeadPotentialTier.GoodCustomer)).Should().Equal(good.Id);
        (await Ids(LeadPotentialTier.None)).Should().BeEquivalentTo([small.Id, person.Id]);
    }

    [Fact]
    public void The_tier_reads_the_thresholds()
    {
        var settings = new KeyAccountSettings(10_000_000m, 50_000_000m, []);
        settings.TierOf(LeadType.Corporate, 50_000_000m).Should().Be(LeadPotentialTier.KeyAccount);
        settings.TierOf(LeadType.Corporate, 10_000_000m).Should().Be(LeadPotentialTier.GoodCustomer);
        settings.TierOf(LeadType.Corporate, 9_999_999m).Should().Be(LeadPotentialTier.None);
        settings.TierOf(LeadType.Individual, 90_000_000m).Should().Be(LeadPotentialTier.None, "a person is never a key account");
        KeyAccountSettings.None.TierOf(LeadType.Corporate, 90_000_000m).Should().Be(LeadPotentialTier.None);
    }
}
