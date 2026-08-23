namespace Sankore.Modules.Workflow.Tests.Features.Instances;

using FluentAssertions;
using NSubstitute;
using Sankore.Modules.Workflow.Domain;
using Sankore.Modules.Workflow.Features.Instances.StartInstance;
using Sankore.Modules.Workflow.Tests.TestSupport;
using Sankore.Shared.Infrastructure.Auth;
using Xunit;

public sealed class StartInstanceHandlerTests : IDisposable
{
    private readonly Guid _tenantId = Guid.NewGuid();
    private readonly TestWorkflowDbContextFactory _factory;

    public StartInstanceHandlerTests() => _factory = new(_tenantId);
    public void Dispose() => _factory.Dispose();

    private StartInstanceHandler BuildHandler()
    {
        var cu = Substitute.For<ICurrentUser>();
        cu.TenantId.Returns(_tenantId);
        cu.Id.Returns(Guid.NewGuid());
        return new StartInstanceHandler(_factory.CreateContext(), cu);
    }

    private async Task<WorkflowTemplate> SeedActiveTemplate(string entityType = "Lead")
    {
        var t = WorkflowTemplate.Create(_tenantId, entityType, "Circuit", Guid.NewGuid());
        t.AddStep(1, "Manager approval", approverRoleCode: "branchmanager");
        t.AddStep(2, "Director approval", approverRoleCode: "director");
        t.Activate();

        await using var seed = _factory.CreateContext();
        seed.WorkflowTemplates.Add(t);
        seed.WorkflowStepDefinitions.AddRange(t.Steps);
        await seed.SaveChangesAsync();
        return t;
    }

    // ── S1: Happy path — instance created, steps materialised ────────────

    [Fact]
    public async Task Starts_instance_and_creates_steps()
    {
        await SeedActiveTemplate();
        var entityId = Guid.NewGuid();

        var result = await BuildHandler().Handle(
            new StartInstanceCommand("Lead", entityId), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().NotBe(Guid.Empty);

        await using var verify = _factory.CreateContext();
        var instance = verify.WorkflowInstances.FirstOrDefault(i => i.Id == result.Value);
        instance.Should().NotBeNull();
        instance!.EntityId.Should().Be(entityId);
        instance.Status.Should().Be(WorkflowStatus.InProgress);
        instance.CurrentStepOrder.Should().Be(1);

        verify.WorkflowInstanceSteps
            .Where(s => s.InstanceId == result.Value)
            .Should().HaveCount(2);
    }

    // ── S2: No active template for entity type ────────────────────────────

    [Fact]
    public async Task Fails_when_no_active_template_exists()
    {
        var result = await BuildHandler().Handle(
            new StartInstanceCommand("Lead", Guid.NewGuid()), CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain("No active workflow template");
    }

    // ── S3: Tenant isolation — wrong-tenant template not found ─────────────

    [Fact]
    public async Task Fails_when_template_belongs_to_different_tenant()
    {
        // Seed template in different tenant — the global query filter must block it
        var otherTenantId = Guid.NewGuid();
        var otherTemplate = WorkflowTemplate.Create(otherTenantId, "Lead", "Circuit", Guid.NewGuid());
        otherTemplate.AddStep(1, "Step 1");
        otherTemplate.Activate();

        await using var seed = _factory.CreateContext();
        // Use IgnoreQueryFilters to insert into the shared InMemory db
        seed.WorkflowTemplates.Add(otherTemplate);
        seed.WorkflowStepDefinitions.AddRange(otherTemplate.Steps);
        await seed.SaveChangesAsync();

        // Our handler context is scoped to _tenantId — it must NOT see otherTenantId's template
        var result = await BuildHandler().Handle(
            new StartInstanceCommand("Lead", Guid.NewGuid()), CancellationToken.None);

        result.IsFailure.Should().BeTrue();
    }
}
