namespace Sankore.Modules.Workflow.Tests.Infrastructure;

using FluentAssertions;
using MediatR;
using Microsoft.EntityFrameworkCore;
using NSubstitute;
using Sankore.Modules.Workflow.Features.Templates.ActivateTemplate;
using Sankore.Modules.Workflow.Features.Templates.AddStep;
using Sankore.Modules.Workflow.Features.Templates.CreateTemplate;
using Sankore.Modules.Workflow.PublicApi;
using Sankore.Modules.Workflow.Tests.TestSupport;
using Sankore.Shared.Infrastructure.Auth;
using Xunit;

/// <summary>A ready-made project circuit goes through the commands a hand-drawn one does (2026-11-05).</summary>
public sealed class ProjectCircuitProvisioningTests : IDisposable
{
    private readonly Guid _tenantId = Guid.NewGuid();
    private readonly TestWorkflowDbContextFactory _factory;
    private readonly ISender _sender = Substitute.For<ISender>();

    public ProjectCircuitProvisioningTests()
    {
        _factory = new(_tenantId);
        var user = Substitute.For<ICurrentUser>();
        user.TenantId.Returns(_tenantId);
        user.Id.Returns(Guid.NewGuid());

        _sender.Send(Arg.Any<CreateTemplateCommand>(), Arg.Any<CancellationToken>()).Returns(ci =>
            new CreateTemplateHandler(_factory.CreateContext(), user).Handle(ci.Arg<CreateTemplateCommand>(), default));
        _sender.Send(Arg.Any<AddStepCommand>(), Arg.Any<CancellationToken>()).Returns(ci =>
            new AddStepHandler(_factory.CreateContext()).Handle(ci.Arg<AddStepCommand>(), default));
        _sender.Send(Arg.Any<ActivateTemplateCommand>(), Arg.Any<CancellationToken>()).Returns(ci =>
            new ActivateTemplateHandler(_factory.CreateContext()).Handle(ci.Arg<ActivateTemplateCommand>(), default));
    }

    public void Dispose() => _factory.Dispose();

    private Task<Sankore.Shared.Kernel.Result<string>> EnsureAsync() =>
        new WorkflowModuleFacade(_factory.CreateContext(), null, _sender).EnsureProjectCircuitAsync(
            "Analyse et décision de crédit", null,
            [new("Avis de l'analyste", "CreditAnalyst", 48), new("Décision", "BranchManager", 48)]);

    [Fact]
    public async Task The_circuit_is_created_active_with_its_steps_in_order()
    {
        var circuit = await EnsureAsync();

        circuit.IsSuccess.Should().BeTrue(circuit.Error);
        await using var db = _factory.CreateContext();
        var template = await db.WorkflowTemplates.Include(t => t.Steps).SingleAsync();
        template.IsActive.Should().BeTrue();
        template.Circuit.Should().Be(circuit.Value);
        template.Steps.OrderBy(s => s.Order).Select(s => s.ApproverRoleCode).Should().Equal("CreditAnalyst", "BranchManager");
    }

    [Fact]
    public async Task A_second_call_reuses_it()
    {
        var first = await EnsureAsync();
        var second = await EnsureAsync();

        second.Value.Should().Be(first.Value);
        await using var db = _factory.CreateContext();
        (await db.WorkflowTemplates.CountAsync()).Should().Be(1);
    }
}
