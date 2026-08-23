namespace Sankore.Modules.Workflow.Tests.Features.Templates;

using FluentAssertions;
using Sankore.Modules.Workflow.Domain;
using Sankore.Modules.Workflow.Features.Templates.ActivateTemplate;
using Sankore.Modules.Workflow.Tests.TestSupport;
using Xunit;

public sealed class ActivateTemplateHandlerTests : IDisposable
{
    private readonly Guid _tenantId = Guid.NewGuid();
    private readonly TestWorkflowDbContextFactory _factory;

    public ActivateTemplateHandlerTests() => _factory = new(_tenantId);
    public void Dispose() => _factory.Dispose();

    private ActivateTemplateHandler BuildHandler() =>
        new(_factory.CreateContext());

    private async Task<WorkflowTemplate> SeedTemplate(bool addStep = true, bool active = false)
    {
        var t = WorkflowTemplate.Create(_tenantId, "Lead", "Circuit", Guid.NewGuid());
        if (addStep) t.AddStep(1, "Manager approval", approverRoleCode: "branchmanager");
        if (active) t.Activate();

        await using var seed = _factory.CreateContext();
        seed.WorkflowTemplates.Add(t);
        await seed.SaveChangesAsync();
        return t;
    }

    // ── S1: Happy path ────────────────────────────────────────────────────

    [Fact]
    public async Task Activates_template_with_steps()
    {
        var t = await SeedTemplate();

        var result = await BuildHandler().Handle(
            new ActivateTemplateCommand(t.Id), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        await using var verify = _factory.CreateContext();
        verify.WorkflowTemplates.Single(x => x.Id == t.Id).IsActive.Should().BeTrue();
    }

    // ── S2: No steps → cannot activate ───────────────────────────────────

    [Fact]
    public async Task Fails_when_template_has_no_steps()
    {
        var t = await SeedTemplate(addStep: false);

        var result = await BuildHandler().Handle(
            new ActivateTemplateCommand(t.Id), CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain("no steps");
    }

    // ── S3: Already active → error ────────────────────────────────────────

    [Fact]
    public async Task Fails_when_already_active()
    {
        var t = await SeedTemplate(addStep: true, active: true);

        var result = await BuildHandler().Handle(
            new ActivateTemplateCommand(t.Id), CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain("already active");
    }

    // ── S4: Conflict with another active template ─────────────────────────

    [Fact]
    public async Task Fails_when_another_active_template_exists_for_same_entity_type()
    {
        var t1 = await SeedTemplate(addStep: true, active: true); // active

        // Second template for same entity type
        var t2 = WorkflowTemplate.Create(_tenantId, "Lead", "Alt circuit", Guid.NewGuid());
        t2.AddStep(1, "Step 1");
        await using var seed = _factory.CreateContext();
        seed.WorkflowTemplates.Add(t2);
        await seed.SaveChangesAsync();

        var result = await BuildHandler().Handle(
            new ActivateTemplateCommand(t2.Id), CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain("already exists");
    }

    // ── S5: Not found ─────────────────────────────────────────────────────

    [Fact]
    public async Task Fails_when_template_not_found()
    {
        var result = await BuildHandler().Handle(
            new ActivateTemplateCommand(Guid.NewGuid()), CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain("not found");
    }
}
