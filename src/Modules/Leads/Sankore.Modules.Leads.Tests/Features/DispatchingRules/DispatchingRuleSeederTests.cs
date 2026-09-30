namespace Sankore.Modules.Leads.Tests.Features.DispatchingRules;

using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Sankore.Modules.Leads.Domain;
using Sankore.Modules.Leads.Features.DispatchingRules;
using Sankore.Modules.Leads.Infrastructure;
using Sankore.Modules.Leads.Tests.TestSupport;
using Sankore.Shared.Kernel;
using Xunit;

/// <summary>
/// The seeder only makes visible what dispatching already did: an administrator used to open an
/// empty rules screen while their leads were routed with a 2-hour SLA and a 20-task ceiling they
/// had never chosen.
/// </summary>
public sealed class DispatchingRuleSeederTests : IDisposable
{
    private readonly Guid _tenantId = Guid.NewGuid();
    private readonly TestDbContextFactory _factory;

    public DispatchingRuleSeederTests() => _factory = new TestDbContextFactory(_tenantId);

    public void Dispose() => _factory.Dispose();

    private static ITenantStore StoreOf(params Guid[] tenantIds)
    {
        var store = Substitute.For<ITenantStore>();
        store.GetAllActiveAsync(Arg.Any<CancellationToken>())
            .Returns(tenantIds
                .Select(id => new TenantInfo(
                    Id: id,
                    Name: $"IMF {id:N}"[..12],
                    Fqdn: $"{id:N}.sankore.local",
                    IsActive: true,
                    IsMaintenance: false,
                    TrialExpiresAt: null,
                    BlockedAt: null))
                .ToList());
        return store;
    }

    private static Task SeedAsync(LeadsDbContext db, ITenantStore store) =>
        DispatchingRuleSeeder.SeedAsync(db, store, NullLogger.Instance);

    private static DispatchingRule Custom(Guid tenantId, string name, int priority) =>
        DispatchingRule.Create(
            tenantId, name, DispatchingStrategy.RoundRobin,
            new ScoringWeights(Language: 25, Product: 25, Geography: 20, Workload: 15, Performance: 15),
            maxLeadsPerAgent: 10, antiMonopolyThreshold: 3,
            firstContactSla: TimeSpan.FromMinutes(30), priority: priority);

    [Fact]
    public async Task Seeds_the_default_rule_for_a_tenant_that_has_none()
    {
        await using var db = _factory.CreateContext();

        await SeedAsync(db, StoreOf(_tenantId));

        var rule = await db.DispatchingRules.IgnoreQueryFilters().SingleAsync();
        rule.TenantId.Should().Be(_tenantId);
        rule.Name.Should().Be(DispatchingRuleSeeder.DefaultRuleName);
        rule.IsActive.Should().BeTrue();
    }

    [Fact]
    public async Task The_seeded_rule_carries_exactly_the_built_in_default_values()
    {
        // The whole point: visibility without a behaviour change.
        await using var db = _factory.CreateContext();
        var builtIn = DispatchingRule.Default();

        await SeedAsync(db, StoreOf(_tenantId));

        var rule = await db.DispatchingRules.IgnoreQueryFilters().SingleAsync();
        rule.Strategy.Should().Be(builtIn.Strategy);
        rule.Weights.Should().Be(builtIn.Weights);
        rule.MaxLeadsPerAgent.Should().Be(builtIn.MaxLeadsPerAgent);
        rule.MaxTasksPerAgent.Should().Be(builtIn.MaxTasksPerAgent);
        rule.AntiMonopolyThreshold.Should().Be(builtIn.AntiMonopolyThreshold);
        rule.FirstContactSla.Should().Be(builtIn.FirstContactSla);
        rule.DeclineExclusionTtl.Should().Be(builtIn.DeclineExclusionTtl);
    }

    [Fact]
    public async Task Running_it_twice_adds_nothing()
    {
        await using var db = _factory.CreateContext();

        await SeedAsync(db, StoreOf(_tenantId));
        await SeedAsync(db, StoreOf(_tenantId));

        (await db.DispatchingRules.IgnoreQueryFilters().CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task A_tenant_that_configured_its_own_rules_is_left_alone()
    {
        // Adding a default beside them would change which rule wins on priority.
        await using var db = _factory.CreateContext();
        db.DispatchingRules.Add(Custom(_tenantId, "Ma règle", priority: 5));
        await db.SaveChangesAsync();

        await SeedAsync(db, StoreOf(_tenantId));

        var rules = await db.DispatchingRules.IgnoreQueryFilters().ToListAsync();
        rules.Should().ContainSingle().Which.Name.Should().Be("Ma règle");
    }

    [Fact]
    public async Task A_tenant_whose_only_rule_is_deactivated_is_also_left_alone()
    {
        // Deactivating every rule is a decision; a restart must not undo it.
        await using var db = _factory.CreateContext();
        var rule = Custom(_tenantId, "Suspendue", priority: 0);
        rule.Deactivate();
        db.DispatchingRules.Add(rule);
        await db.SaveChangesAsync();

        await SeedAsync(db, StoreOf(_tenantId));

        (await db.DispatchingRules.IgnoreQueryFilters().CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task Each_tenant_gets_its_own_rule()
    {
        await using var db = _factory.CreateContext();
        var second = Guid.NewGuid();
        var third = Guid.NewGuid();
        db.DispatchingRules.Add(Custom(third, "Déjà configurée", priority: 1));
        await db.SaveChangesAsync();

        await SeedAsync(db, StoreOf(_tenantId, second, third));

        var rules = await db.DispatchingRules.IgnoreQueryFilters().ToListAsync();
        rules.Should().HaveCount(3);
        rules.Where(r => r.Name == DispatchingRuleSeeder.DefaultRuleName)
             .Select(r => r.TenantId)
             .Should().BeEquivalentTo([_tenantId, second]);
    }

    [Fact]
    public async Task No_active_tenant_means_nothing_to_seed()
    {
        await using var db = _factory.CreateContext();

        await SeedAsync(db, StoreOf());

        (await db.DispatchingRules.IgnoreQueryFilters().CountAsync()).Should().Be(0);
    }
}
