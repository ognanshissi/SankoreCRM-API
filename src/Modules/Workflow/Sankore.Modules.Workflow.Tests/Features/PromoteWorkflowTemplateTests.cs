namespace Sankore.Modules.Workflow.Tests.Features;

using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using NSubstitute;
using Sankore.Modules.Workflow.Domain;
using Sankore.Modules.Workflow.Features.Templates.Promote;
using Sankore.Modules.Workflow.Tests.TestSupport;
using Sankore.Shared.Infrastructure.Auth;
using Sankore.Shared.Infrastructure.TestMode;
using Xunit;

/// <summary>
/// Sandbox lot 4, « Copier en production » for a workflow: a new INACTIVE version of the same
/// circuit in production, steps and triggers included; a draft production never ran is replaced
/// rather than stacked, an active version is never touched.
/// </summary>
public sealed class PromoteWorkflowTemplateTests : IDisposable
{
    private readonly Guid _live = Guid.NewGuid();
    private readonly Guid _twin = Guid.NewGuid();
    private readonly TestWorkflowDbContextFactory _factory;
    private readonly ICurrentUser _me = Substitute.For<ICurrentUser>();

    public PromoteWorkflowTemplateTests()
    {
        _factory = new TestWorkflowDbContextFactory(_twin);
        _me.Id.Returns(Guid.NewGuid());
        _me.TenantId.Returns(_twin);
        _me.TeamTenantId.Returns(_live);
    }

    public void Dispose() => _factory.Dispose();

    private async Task<Guid> SeedAsync(Guid tenantId, string stepName, bool active = false, int version = 1)
    {
        var template = WorkflowTemplate.Create(tenantId, "Lead", "Qualification", Guid.NewGuid());
        for (var v = 1; v < version; v++) template = template.CreateDraft(Guid.NewGuid());
        template.AddStep(1, stepName, approverRoleCode: "BranchManager");
        if (active) template.Activate();

        await using var db = _factory.CreateContext();
        db.WorkflowTemplates.Add(template);
        db.WorkflowTriggers.Add(WorkflowTrigger.Create(tenantId, template.Id, TriggerType.EntityEvent, "lead.qualified"));
        await db.SaveChangesAsync();
        return template.Id;
    }

    private async Task<PromotionResult> PromoteAsync(Guid id, bool dryRun = false)
    {
        await using var db = _factory.CreateContext();
        var result = await new PromoteWorkflowTemplateHandler(_me, new WorkflowTemplateCopier(db)).Handle(new PromoteWorkflowTemplateCommand(id, dryRun), default);
        result.IsSuccess.Should().BeTrue(result.Error);
        return result.Value;
    }

    private async Task<List<WorkflowTemplate>> LiveAsync()
    {
        await using var db = _factory.CreateContext();
        return await db.WorkflowTemplates.IgnoreQueryFilters().Include(t => t.Steps)
            .Where(t => t.TenantId == _live).OrderBy(t => t.Version).ToListAsync();
    }

    [Fact]
    public async Task A_circuit_new_to_production_arrives_inactive_with_its_steps_and_triggers()
    {
        var test = await SeedAsync(_twin, "Visa chef d’agence");

        var result = await PromoteAsync(test);

        result.Action.Should().Be(PromotionActions.Created);
        var copy = (await LiveAsync()).Should().ContainSingle().Subject;
        copy.Id.Should().Be(result.ProductionId!.Value);
        copy.IsActive.Should().BeFalse("production activates it itself, after reading it");
        copy.Version.Should().Be(1);
        copy.Steps.Select(s => s.Name).Should().Equal("Visa chef d’agence");

        await using var db = _factory.CreateContext();
        (await db.WorkflowTriggers.IgnoreQueryFilters().CountAsync(t => t.TemplateId == copy.Id && t.TenantId == _live)).Should().Be(1);
    }

    [Fact]
    public async Task A_second_copy_replaces_the_draft_production_never_ran()
    {
        var test = await SeedAsync(_twin, "Visa chef d’agence");
        await PromoteAsync(test);

        await PromoteAsync(test);

        (await LiveAsync()).Should().ContainSingle().Which.Version.Should().Be(1);
    }

    [Fact]
    public async Task Productions_active_version_stays_and_the_copy_comes_next()
    {
        var active = await SeedAsync(_live, "Ancienne étape", active: true);
        var test = await SeedAsync(_twin, "Nouvelle étape");

        var result = await PromoteAsync(test);

        result.Action.Should().Be(PromotionActions.Updated);
        var live = await LiveAsync();
        live.Should().HaveCount(2);
        live[0].Id.Should().Be(active);
        live[0].IsActive.Should().BeTrue();
        live[1].Version.Should().Be(2);
        live[1].IsActive.Should().BeFalse();
    }

    [Fact]
    public async Task A_refresh_of_the_test_mode_activates_the_copy_and_retires_its_own_version()
    {
        var mine = await SeedAsync(_twin, "Ancienne étape d’essai", active: true);
        var production = await SeedAsync(_live, "Visa chef d’agence", active: true);

        await using var db = _factory.CreateContext();
        var copier = new WorkflowTemplateCopier(db);
        (await copier.ListAsync(_live, default)).Should().Equal(production);
        var result = await copier.CopyAsync(new ConfigurationCopy(_live, production, _twin, Guid.NewGuid(), "Awa", false, ToProduction: false), default);

        result.IsSuccess.Should().BeTrue(result.Error);
        await using var check = _factory.CreateContext();
        var twin = await check.WorkflowTemplates.IgnoreQueryFilters().Include(t => t.Steps)
            .Where(t => t.TenantId == _twin).OrderBy(t => t.Version).ToListAsync();
        twin.Single(t => t.Id == mine).IsActive.Should().BeFalse();
        twin.Single(t => t.IsActive).Steps.Select(s => s.Name).Should().Equal("Visa chef d’agence");
    }

    [Fact]
    public async Task A_dry_run_writes_nothing()
    {
        var test = await SeedAsync(_twin, "Visa chef d’agence");

        (await PromoteAsync(test, dryRun: true)).Action.Should().Be(PromotionActions.Created);

        (await LiveAsync()).Should().BeEmpty();
    }
}
