namespace Sankore.Modules.Workflow.Tests.Infrastructure;

using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Sankore.Modules.Workflow.Domain;
using Sankore.Modules.Workflow.Infrastructure.Seeders;
using Sankore.Modules.Workflow.Tests.TestSupport;
using Sankore.Shared.Kernel;
using Xunit;

/// <summary>
/// M02 has called <c>StartWorkflowAsync("KycFile", …)</c> since the approval circuit was written,
/// against a template nothing ever created — and because the call degrades to a log line, the gap
/// was invisible. These tests pin the seeder that closes it, and the rule that it must not undo an
/// administrator's decision.
/// </summary>
public sealed class WorkflowTemplateSeederTests : IDisposable
{
    private readonly Guid _tenantA = Guid.NewGuid();
    private readonly Guid _tenantB = Guid.NewGuid();
    private readonly TestWorkflowDbContextFactory _factory;

    public WorkflowTemplateSeederTests() => _factory = new(_tenantA);
    public void Dispose() => _factory.Dispose();

    private ITenantStore Tenants(params Guid[] ids)
    {
        var store = Substitute.For<ITenantStore>();
        store.GetAllActiveAsync(Arg.Any<CancellationToken>())
            .Returns(ids
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

    private Task SeedAsync(ITenantStore tenants)
    {
        var db = _factory.CreateContext();
        return WorkflowTemplateSeeder.SeedAsync(db, tenants, NullLogger.Instance);
    }

    private async Task<List<WorkflowTemplate>> KycTemplatesAsync()
    {
        await using var verify = _factory.CreateContext();
        return await verify.WorkflowTemplates
            .IgnoreQueryFilters()
            .Include(t => t.Steps)
            .Where(t => t.EntityType == WorkflowTemplateSeeder.KycFileEntityType)
            .ToListAsync();
    }

    // ── what a fresh tenant gets ────────────────────────────────────────────

    [Fact]
    public async Task A_tenant_with_no_template_gets_an_active_three_rung_ladder()
    {
        await SeedAsync(Tenants(_tenantA));

        var templates = await KycTemplatesAsync();
        templates.Should().HaveCount(1);

        var template = templates[0];
        template.TenantId.Should().Be(_tenantA);
        template.IsActive.Should().BeTrue("the facade resolves templates by IsActive");
        template.Steps.Select(s => s.Order).Order().Should().Equal(1, 2, 3);
    }

    [Fact]
    public async Task Activation_leaves_an_approve_path_on_every_rung()
    {
        // Activate() clears and rebuilds the transition table, so a template activated BEFORE its
        // steps were added would have no APPROVE path and an instance could never advance. This is
        // the test that catches that ordering mistake.
        await SeedAsync(Tenants(_tenantA));

        await using var verify = _factory.CreateContext();
        var template = await verify.WorkflowTemplates
            .IgnoreQueryFilters()
            .Include(t => t.Steps)
            .SingleAsync(t => t.EntityType == WorkflowTemplateSeeder.KycFileEntityType);

        var transitions = await verify.WorkflowTransitions
            .IgnoreQueryFilters()
            .Where(t => t.TemplateId == template.Id)
            .ToListAsync();

        foreach (var step in template.Steps)
        {
            transitions.Should().Contain(
                t => t.FromStateId == step.Id && t.EventCode == EventCodes.Approve,
                $"step {step.Order} needs a way forward");
        }
    }

    [Fact]
    public async Task Every_rung_carries_the_seeded_deadline()
    {
        await SeedAsync(Tenants(_tenantA));

        var template = (await KycTemplatesAsync())[0];

        template.Steps.Should().OnlyContain(
            s => s.TimeoutHours == WorkflowTemplateSeeder.ApprovalStepTimeoutHours);
    }

    [Fact]
    public async Task Each_tenant_gets_its_own()
    {
        await SeedAsync(Tenants(_tenantA, _tenantB));

        (await KycTemplatesAsync()).Select(t => t.TenantId)
            .Should().BeEquivalentTo(new[] { _tenantA, _tenantB });
    }

    // ── idempotency, and not overruling an administrator ────────────────────

    [Fact]
    public async Task Seeding_twice_changes_nothing()
    {
        var tenants = Tenants(_tenantA);
        await SeedAsync(tenants);
        var first = (await KycTemplatesAsync())[0].Id;

        await SeedAsync(tenants);

        var templates = await KycTemplatesAsync();
        templates.Should().HaveCount(1);
        templates[0].Id.Should().Be(first, "the same template, not a replacement");
    }

    [Fact]
    public async Task A_template_an_administrator_deactivated_is_not_replaced()
    {
        // Deactivating is a decision. Re-seeding around it would have start-up quietly overrule an
        // administrator — the rule M13's DispatchingRuleSeeder already follows — which is why the
        // check ignores IsActive and looks for any template at all.
        var existing = WorkflowTemplate.Create(
            _tenantA, WorkflowTemplateSeeder.KycFileEntityType, "Le nôtre", Guid.NewGuid());
        existing.AddStep(1, "Agent");

        await using (var seed = _factory.CreateContext())
        {
            seed.WorkflowTemplates.Add(existing);
            await seed.SaveChangesAsync();
        }

        await SeedAsync(Tenants(_tenantA));

        var templates = await KycTemplatesAsync();
        templates.Should().HaveCount(1);
        templates[0].Name.Should().Be("Le nôtre");
        templates[0].IsActive.Should().BeFalse("the seeder must not activate what was switched off");
    }

    [Fact]
    public async Task No_active_tenant_means_nothing_to_seed()
    {
        await SeedAsync(Tenants());

        (await KycTemplatesAsync()).Should().BeEmpty();
    }
}
