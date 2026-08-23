namespace Sankore.Modules.Workflow.Tests.Features.Templates;

using FluentAssertions;
using Sankore.Modules.Workflow.Domain;
using Sankore.Modules.Workflow.Features.Templates.AddStep;
using Sankore.Modules.Workflow.Tests.TestSupport;
using Xunit;

public sealed class AddStepHandlerTests : IDisposable
{
    private readonly Guid _tenantId = Guid.NewGuid();
    private readonly TestWorkflowDbContextFactory _factory;

    public AddStepHandlerTests() => _factory = new(_tenantId);
    public void Dispose() => _factory.Dispose();

    private AddStepHandler BuildHandler() => new(_factory.CreateContext());

    private async Task<WorkflowTemplate> SeedDraftTemplate()
    {
        var t = WorkflowTemplate.Create(_tenantId, "Lead", "Circuit", Guid.NewGuid());
        await using var seed = _factory.CreateContext();
        seed.WorkflowTemplates.Add(t);
        await seed.SaveChangesAsync();
        return t;
    }

    private async Task<WorkflowTemplate> SeedActiveTemplate()
    {
        var t = WorkflowTemplate.Create(_tenantId, "Lead", "Circuit", Guid.NewGuid());
        t.AddStep(1, "Step 1");
        t.Activate();
        await using var seed = _factory.CreateContext();
        seed.WorkflowTemplates.Add(t);
        await seed.SaveChangesAsync();
        return t;
    }

    // ── S1: Happy path ────────────────────────────────────────────────────

    [Fact]
    public async Task Adds_step_and_returns_step_id()
    {
        var t = await SeedDraftTemplate();

        var result = await BuildHandler().Handle(
            new AddStepCommand(t.Id, 1, "Manager approval", null, "branchmanager", 48),
            CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().NotBe(Guid.Empty);

        await using var verify = _factory.CreateContext();
        verify.WorkflowStepDefinitions
            .Where(s => s.TemplateId == t.Id)
            .Should().ContainSingle(s => s.Order == 1 && s.ApproverRoleCode == "branchmanager");
    }

    // ── S2: Cannot add to active template ────────────────────────────────

    [Fact]
    public async Task Fails_when_template_is_active()
    {
        var t = await SeedActiveTemplate();

        var result = await BuildHandler().Handle(
            new AddStepCommand(t.Id, 2, "Another step", null, null, null),
            CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain("active template");
    }

    // ── S3: Duplicate order rejected ─────────────────────────────────────

    [Fact]
    public async Task Fails_when_step_order_is_duplicate()
    {
        var t = await SeedDraftTemplate();
        await BuildHandler().Handle(
            new AddStepCommand(t.Id, 1, "Step A", null, null, null), CancellationToken.None);

        var result = await BuildHandler().Handle(
            new AddStepCommand(t.Id, 1, "Step B", null, null, null), CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain("order 1");
    }

    // ── S4: Template not found ────────────────────────────────────────────

    [Fact]
    public async Task Fails_when_template_not_found()
    {
        var result = await BuildHandler().Handle(
            new AddStepCommand(Guid.NewGuid(), 1, "Step", null, null, null),
            CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain("not found");
    }
}
