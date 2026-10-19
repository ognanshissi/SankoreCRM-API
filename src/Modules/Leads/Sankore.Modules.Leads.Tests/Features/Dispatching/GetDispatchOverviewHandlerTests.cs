namespace Sankore.Modules.Leads.Tests.Features.Dispatching;

using FluentAssertions;
using NSubstitute;
using Sankore.Modules.Administration.PublicApi;
using Sankore.Modules.Leads.Domain;
using Sankore.Modules.Leads.Features.Dispatching.GetDispatchOverview;
using Sankore.Modules.Leads.Tests.TestSupport;
using Sankore.Shared.Kernel;
using Sankore.Shared.Kernel.ValueObject;
using Xunit;

/// <summary>
/// The dispatching screen must say what each rule really does: only the highest-priority active
/// rule routes leads that no source pins, and a second active rule otherwise routes nothing.
/// </summary>
public sealed class GetDispatchOverviewHandlerTests : IDisposable
{
    private readonly Guid _tenantId = Guid.NewGuid();
    private readonly Guid _awa = Guid.NewGuid();
    private readonly Guid _koffi = Guid.NewGuid();
    private readonly TestDbContextFactory _db;
    private readonly IAdministrationModule _admin = Substitute.For<IAdministrationModule>();

    private static readonly DateTimeOffset From = new(2026, 10, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset To = new(2026, 10, 8, 0, 0, 0, TimeSpan.Zero);

    public GetDispatchOverviewHandlerTests()
    {
        _db = new TestDbContextFactory(_tenantId);
        _admin.GetUserNamesAsync(_tenantId, Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<Guid, string> { [_awa] = "Awa Sangaré", [_koffi] = "Koffi Yao" });
    }

    public void Dispose() => _db.Dispose();

    [Fact]
    public async Task Only_the_highest_priority_active_rule_applies_and_a_lower_one_is_flagged_as_never_reached()
    {
        var top = await AddRuleAsync("Score de compatibilité", priority: 1);
        var low = await AddRuleAsync("Tour de rôle", priority: 0);
        var off = await AddRuleAsync("Ancienne règle", priority: 5, active: false);

        var overview = await RunAsync();

        Usage(overview, top).Should().Be(DispatchRuleUsage.AppliesByDefault);
        Usage(overview, low).Should().Be(DispatchRuleUsage.NeverReached);
        Usage(overview, off).Should().Be(DispatchRuleUsage.Inactive);
        overview.Rules.Should().NotContain(r => r.Kind == DispatchBucketKind.BuiltInDefault,
            "the built-in default routes nothing while a rule is active");
        overview.Rules[0].RuleId.Should().Be(top.Id, "rules are listed in the order the resolver considers them");
    }

    [Fact]
    public async Task Without_any_active_rule_the_built_in_default_is_the_one_that_applies()
    {
        var overview = await RunAsync();

        overview.Rules.Should().ContainSingle(r => r.Kind == DispatchBucketKind.BuiltInDefault)
            .Which.Usage.Should().Be(DispatchRuleUsage.AppliesByDefault);
    }

    [Fact]
    public async Task A_rule_counts_what_it_routed_per_agent_with_contact_and_reassignment()
    {
        var rule = await AddRuleAsync("Score", priority: 1);
        var l1 = await AddLeadAsync(From.AddHours(1));
        var l2 = await AddLeadAsync(From.AddHours(2));
        var l3 = await AddLeadAsync(From.AddHours(3));

        await AddAssignmentAsync(l1, _awa, From.AddHours(1).AddMinutes(10), rule.Id, contacted: true);
        await AddAssignmentAsync(l2, _awa, From.AddHours(2).AddMinutes(30), rule.Id, contacted: false);
        await AddAssignmentAsync(l3, _koffi, From.AddHours(3).AddMinutes(20), rule.Id, contacted: false, superseded: true);
        // Outside the period: not counted.
        await AddAssignmentAsync(l1, _koffi, To.AddDays(1), rule.Id, contacted: false);

        var bucket = (await RunAsync()).Rules.Single(r => r.RuleId == rule.Id);

        bucket.Dispatched.Should().Be(3);
        bucket.Reassigned.Should().Be(1);
        bucket.MedianDelayMinutes.Should().Be(20);
        var awa = bucket.Agents.Single(a => a.AgentId == _awa);
        awa.Received.Should().Be(2);
        awa.Contacted.Should().Be(1);
        awa.Waiting.Should().Be(1);
        bucket.Agents.Single(a => a.AgentId == _koffi).Waiting.Should().Be(0, "a reassigned lead is no longer theirs to contact");
        bucket.Recent.Select(r => r.Outcome).Should().BeEquivalentTo(
            [DispatchOutcome.Reassigned, DispatchOutcome.ToContact, DispatchOutcome.Contacted],
            o => o.WithStrictOrdering());
    }

    [Fact]
    public async Task Manual_assignments_get_their_own_bucket()
    {
        await AddRuleAsync("Score", priority: 1);
        var lead = await AddLeadAsync(From.AddHours(1));
        await using (var db = _db.CreateContext())
        {
            db.LeadAssignments.Add(LeadAssignment.CreateManualOverride(
                _tenantId, lead.Id, _awa, "client connu", From.AddHours(5), From.AddHours(2)));
            await db.SaveChangesAsync();
        }

        var manual = (await RunAsync()).Rules.Single(r => r.Kind == DispatchBucketKind.Manual);

        manual.Dispatched.Should().Be(1);
        manual.Agents.Single().Name.Should().Be("Awa Sangaré");
    }

    // ── Harness ─────────────────────────────────────────────────────────────

    private static DispatchRuleUsage Usage(DispatchOverviewDto o, DispatchingRule rule) =>
        o.Rules.Single(r => r.RuleId == rule.Id).Usage;

    private async Task<DispatchOverviewDto> RunAsync()
    {
        await using var db = _db.CreateContext();
        var result = await new GetDispatchOverviewHandler(db, new FixedTenantContext(_tenantId), _admin)
            .Handle(new GetDispatchOverviewQuery(From, To), default);
        result.IsSuccess.Should().BeTrue();
        return result.Value!;
    }

    private async Task<DispatchingRule> AddRuleAsync(string name, int priority, bool active = true)
    {
        var rule = DispatchingRule.Create(
            _tenantId, name, DispatchingStrategy.CompatibilityScoring,
            new ScoringWeights(Language: 25, Product: 25, Geography: 20, Workload: 15, Performance: 15),
            maxLeadsPerAgent: 30, antiMonopolyThreshold: 5, firstContactSla: TimeSpan.FromHours(4), priority: priority);
        if (!active) rule.Deactivate();
        await using var db = _db.CreateContext();
        db.DispatchingRules.Add(rule);
        await db.SaveChangesAsync();
        return rule;
    }

    private async Task<Lead> AddLeadAsync(DateTimeOffset capturedAt)
    {
        var lead = LeadTestBuilder.Create().WithTenant(_tenantId).Build();
        typeof(Lead).GetProperty(nameof(Lead.CapturedAt))!.SetValue(lead, capturedAt);
        await using var db = _db.CreateContext();
        db.Leads.Add(lead);
        await db.SaveChangesAsync();
        return lead;
    }

    private async Task AddAssignmentAsync(
        Lead lead, Guid agentId, DateTimeOffset at, Guid ruleId, bool contacted, bool superseded = false)
    {
        var assignment = LeadAssignment.Create(
            _tenantId, lead.Id, agentId, DispatchingStrategy.CompatibilityScoring, 60,
            slaDeadline: at.AddHours(4), createdAt: at, ruleId: ruleId);
        if (contacted) assignment.RecordFirstContact(at.AddMinutes(30), FirstContactSource.AgentDeclared);
        if (superseded) assignment.Supersede(at.AddHours(1));

        await using var db = _db.CreateContext();
        db.LeadAssignments.Add(assignment);
        await db.SaveChangesAsync();
    }
}
