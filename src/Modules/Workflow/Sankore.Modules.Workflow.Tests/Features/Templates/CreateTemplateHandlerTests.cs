namespace Sankore.Modules.Workflow.Tests.Features.Templates;

using FluentAssertions;
using NSubstitute;
using Sankore.Modules.Workflow.Features.Templates.CreateTemplate;
using Sankore.Modules.Workflow.Tests.TestSupport;
using Sankore.Shared.Infrastructure.Auth;
using Xunit;

public sealed class CreateTemplateHandlerTests : IDisposable
{
    private readonly Guid _tenantId = Guid.NewGuid();
    private readonly TestWorkflowDbContextFactory _factory;

    public CreateTemplateHandlerTests() => _factory = new(_tenantId);
    public void Dispose() => _factory.Dispose();

    private CreateTemplateHandler BuildHandler()
    {
        var cu = Substitute.For<ICurrentUser>();
        cu.TenantId.Returns(_tenantId);
        cu.Id.Returns(Guid.NewGuid());
        return new CreateTemplateHandler(_factory.CreateContext(), cu);
    }

    // ── S1: Happy path ────────────────────────────────────────────────────

    [Fact]
    public async Task Creates_template_and_returns_id()
    {
        var result = await BuildHandler().Handle(
            new CreateTemplateCommand("Lead", "Lead approval", null),
            CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().NotBe(Guid.Empty);

        await using var verify = _factory.CreateContext();
        verify.WorkflowTemplates.Should().ContainSingle(t =>
            t.EntityType == "Lead" && !t.IsActive);
    }

    // ── S2: Duplicate entity type rejected ───────────────────────────────

    [Fact]
    public async Task Fails_when_template_for_entity_type_already_exists()
    {
        // Seed an existing template
        await BuildHandler().Handle(
            new CreateTemplateCommand("Lead", "First circuit", null),
            CancellationToken.None);

        var result = await BuildHandler().Handle(
            new CreateTemplateCommand("Lead", "Second circuit", null),
            CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain("already exists");
    }

    // ── S3: Different entity types can coexist ────────────────────────────

    [Fact]
    public async Task Different_entity_types_can_coexist()
    {
        var r1 = await BuildHandler().Handle(
            new CreateTemplateCommand("Lead", "Lead circuit", null), CancellationToken.None);
        var r2 = await BuildHandler().Handle(
            new CreateTemplateCommand("Opportunity", "Opp circuit", null), CancellationToken.None);

        r1.IsSuccess.Should().BeTrue();
        r2.IsSuccess.Should().BeTrue();
        await using var verify = _factory.CreateContext();
        verify.WorkflowTemplates.Should().HaveCount(2);
    }
}
