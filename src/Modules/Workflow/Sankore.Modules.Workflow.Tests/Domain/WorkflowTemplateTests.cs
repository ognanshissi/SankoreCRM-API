namespace Sankore.Modules.Workflow.Tests.Domain;

using FluentAssertions;
using Sankore.Modules.Workflow.Domain;
using Sankore.Shared.Kernel;
using Xunit;

public sealed class WorkflowTemplateTests
{
    private static readonly Guid _tenantId = Guid.NewGuid();
    private static readonly Guid _userId = Guid.NewGuid();

    private static WorkflowTemplate MakeTemplate(string entityType = "Lead") =>
        WorkflowTemplate.Create(_tenantId, entityType, "Approval circuit", _userId);

    // ── S1: Create sets properties correctly ──────────────────────────────

    [Fact]
    public void Create_sets_properties_and_is_inactive()
    {
        var t = MakeTemplate();

        t.EntityType.Should().Be("Lead");
        t.Name.Should().Be("Approval circuit");
        t.IsActive.Should().BeFalse();
        t.TenantId.Should().Be(_tenantId);
        t.Steps.Should().BeEmpty();
    }

    // ── S2: Activate requires at least one step ───────────────────────────

    [Fact]
    public void Cannot_activate_template_without_steps()
    {
        var t = MakeTemplate();
        var act = () => t.Activate();
        act.Should().Throw<DomainException>().WithMessage("*no steps*");
    }

    // ── S3: AddStep + Activate happy path ─────────────────────────────────

    [Fact]
    public void Activate_succeeds_when_template_has_steps()
    {
        var t = MakeTemplate();
        t.AddStep(1, "Manager approval", approverRoleCode: "branchmanager");

        t.Activate();

        t.IsActive.Should().BeTrue();
        t.Steps.Should().ContainSingle(s => s.Name == "Manager approval");
    }

    // ── S4: Duplicate order rejected ──────────────────────────────────────

    [Fact]
    public void AddStep_rejects_duplicate_order()
    {
        var t = MakeTemplate();
        t.AddStep(1, "Step A");

        var act = () => t.AddStep(1, "Step B");
        act.Should().Throw<DomainException>().WithMessage("*order 1*");
    }

    // ── S5: RemoveStep removes the step ───────────────────────────────────

    [Fact]
    public void RemoveStep_removes_the_step()
    {
        var t = MakeTemplate();
        var step = t.AddStep(1, "Step A");

        t.RemoveStep(step.Id);

        t.Steps.Should().BeEmpty();
    }
}
