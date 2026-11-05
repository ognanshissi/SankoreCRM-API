namespace Sankore.Modules.Workflow.Tests.Features.Instances;

using FluentAssertions;
using MassTransit;
using Microsoft.EntityFrameworkCore;
using NSubstitute;
using Sankore.Modules.Customers.PublicApi;
using Sankore.Modules.Kyc.PublicApi;
using Sankore.Modules.Workflow.Domain;
using Sankore.Modules.Workflow.Features.Instances.ApproveStep;
using Sankore.Modules.Workflow.Features.Instances.ListMySteps;
using Sankore.Modules.Workflow.Features.Instances.RejectStep;
using Sankore.Modules.Workflow.Infrastructure.Conditions;
using Sankore.Modules.Workflow.Infrastructure.Gates;
using Sankore.Modules.Workflow.Tests.TestSupport;
using Sankore.Shared.Infrastructure.Auth;
using Sankore.Shared.Infrastructure.Workflow;
using Sankore.Shared.Kernel;
using Sankore.Shared.Kernel.Authorization;
using Xunit;

/// <summary>
/// A credit file is signed by separate people inside its agency's perimeter (2026-11-05). The role
/// check alone let one person holding both roles give the opinion and take the decision, and a
/// signer from another agency decide on a client they could not read.
/// </summary>
public sealed class SigningGuardTests : IDisposable
{
    private const string Analyst = "creditanalyst";
    private const string Manager = "branchmanager";

    private readonly Guid _tenantId = Guid.NewGuid();
    private readonly Guid _projectId = Guid.NewGuid();
    private readonly Guid _agency = Guid.NewGuid();
    private readonly TestWorkflowDbContextFactory _factory;
    private readonly IWorkflowRecordGate _gate = Substitute.For<IWorkflowRecordGate>();
    private readonly IAgencyScopeProvider _scope = Substitute.For<IAgencyScopeProvider>();
    private readonly ICustomersModule _customers = Substitute.For<ICustomersModule>();

    public SigningGuardTests()
    {
        _factory = new(_tenantId);
        _gate.EntityType.Returns("Opportunity");
        _customers.GetClientSummariesAsync(_tenantId, Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<Guid, ClientSummary>());
    }

    public void Dispose() => _factory.Dispose();

    [Fact]
    public async Task One_person_holding_both_roles_cannot_give_the_opinion_and_take_the_decision()
    {
        HoldsCreditFile();
        var instanceId = await SeedAsync();
        var both = User(Analyst, Manager);
        InAgency(both, _agency);

        (await Approve(instanceId, both)).IsSuccess.Should().BeTrue();

        var refused = await Approve(instanceId, both);
        refused.Error.Should().Be("Vous avez déjà signé l'étape « Avis de l'analyste » de ce dossier de crédit : une autre personne doit signer celle-ci.");
        (await CurrentOrderAsync(instanceId)).Should().Be(2);

        var manager = User(Manager);
        InAgency(manager, _agency);
        (await Approve(instanceId, manager)).IsSuccess.Should().BeTrue();
    }

    [Fact]
    public async Task Who_works_the_file_neither_approves_nor_rejects_it()
    {
        var owner = User(Analyst);
        InAgency(owner, _agency);
        HoldsCreditFile(preparedBy: owner.Id);
        var instanceId = await SeedAsync();

        const string expected = "Vous suivez ce dossier de crédit : une autre personne doit signer cette étape.";
        (await Approve(instanceId, owner)).Error.Should().Be(expected);
        (await Reject(instanceId, owner)).Error.Should().Be(expected);
        (await CurrentOrderAsync(instanceId)).Should().Be(1);
    }

    [Fact]
    public async Task A_signer_outside_the_perimeter_is_refused_and_a_tenant_wide_account_is_not()
    {
        HoldsCreditFile();
        var instanceId = await SeedAsync();
        var outsider = User(Analyst);
        InAgency(outsider, Guid.NewGuid());

        const string expected = "Ce dossier de crédit relève d'une agence hors de votre périmètre : vous ne pouvez pas le signer.";
        (await Approve(instanceId, outsider)).Error.Should().Be(expected);
        (await Reject(instanceId, outsider)).Error.Should().Be(expected);

        var tenantWide = User(Analyst);
        _scope.GetAccessibleAgencyIdsAsync(_tenantId, tenantWide.Id, Arg.Any<CancellationToken>())
            .Returns((IReadOnlySet<Guid>?)null);
        (await Approve(instanceId, tenantWide)).IsSuccess.Should().BeTrue();
    }

    [Fact]
    public async Task The_clients_agency_decides_because_it_is_the_one_that_opens_the_client_record()
    {
        var clientId = Guid.NewGuid();
        var clientAgency = Guid.NewGuid();
        _customers.GetClientSummariesAsync(_tenantId, Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<Guid, ClientSummary>
            {
                [clientId] = new(clientId, "CL-1", "Individual", "Awa Koné", "Active", clientAgency, null, "Approved", "Low", null),
            });
        HoldsCreditFile(clientId: clientId);
        var instanceId = await SeedAsync();

        var ofTheLead = User(Analyst);
        InAgency(ofTheLead, _agency);
        (await Approve(instanceId, ofTheLead)).Error.Should().Contain("hors de votre périmètre");

        var ofTheClient = User(Analyst);
        InAgency(ofTheClient, clientAgency);
        (await Approve(instanceId, ofTheClient)).IsSuccess.Should().BeTrue();
    }

    [Fact]
    public async Task A_hand_over_moves_the_work_without_lifting_the_rule()
    {
        HoldsCreditFile();
        var instanceId = await SeedAsync();
        var handedTo = User();
        InAgency(handedTo, Guid.NewGuid());
        await AssignCurrentStepAsync(instanceId, handedTo.Id);

        (await Approve(instanceId, handedTo)).Error.Should().Contain("hors de votre périmètre");
    }

    [Fact]
    public async Task In_test_mode_the_perimeter_is_read_on_the_team_not_on_the_twins_data()
    {
        HoldsCreditFile();
        var instanceId = await SeedAsync();
        var liveTenant = Guid.NewGuid();
        var signer = User(Analyst);
        signer.TeamTenantId.Returns(liveTenant);
        _scope.GetAccessibleAgencyIdsAsync(liveTenant, signer.Id, Arg.Any<CancellationToken>())
            .Returns(new HashSet<Guid> { _agency });

        (await Approve(instanceId, signer)).IsSuccess.Should().BeTrue();
    }

    [Fact]
    public async Task Any_other_record_keeps_the_role_check_alone()
    {
        // An agent signing a step on their own lead is the owner's choice (2026-10-30).
        _gate.SigningRulesAsync(_tenantId, Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<Guid, RecordSigningRule>());
        var instanceId = await SeedAsync();
        var both = User(Analyst, Manager);

        (await Approve(instanceId, both)).IsSuccess.Should().BeTrue();
        (await Approve(instanceId, both)).IsSuccess.Should().BeTrue();
    }

    [Fact]
    public async Task The_queue_hides_what_a_role_could_only_be_refused_and_keeps_what_was_handed_over()
    {
        HoldsCreditFile();
        var byRole = await SeedAsync();
        var handed = await SeedAsync();
        var outsider = User(Analyst);
        InAgency(outsider, Guid.NewGuid());
        await AssignCurrentStepAsync(handed, outsider.Id);

        var mine = await QueueAsync(outsider);
        mine.Should().ContainSingle().Which.InstanceId.Should().Be(handed);

        var colleague = User(Analyst);
        InAgency(colleague, _agency);
        (await QueueAsync(colleague)).Should().ContainSingle().Which.InstanceId.Should().Be(byRole);
    }

    private void HoldsCreditFile(Guid? preparedBy = null, Guid? clientId = null)
        => _gate.SigningRulesAsync(_tenantId, Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<Guid, RecordSigningRule>
            {
                [_projectId] = new(preparedBy, _agency, clientId),
            });

    private ICurrentUser User(params string[] roles)
    {
        var user = Substitute.For<ICurrentUser>();
        user.TenantId.Returns(_tenantId);
        user.TeamTenantId.Returns(_tenantId);
        user.Id.Returns(Guid.NewGuid());
        user.Roles.Returns(roles.ToList());
        return user;
    }

    private void InAgency(ICurrentUser user, Guid agency)
        => _scope.GetAccessibleAgencyIdsAsync(_tenantId, user.Id, Arg.Any<CancellationToken>())
            .Returns(new HashSet<Guid> { agency });

    private SigningGuard Guard(ICurrentUser user)
        => new(_factory.CreateContext(), new RecordStepGates([_gate], Substitute.For<IKycModule>()), _scope, _customers, user);

    private Task<Result> Approve(Guid instanceId, ICurrentUser user)
        => new ApproveStepHandler(_factory.CreateContext(), user, Substitute.For<IBus>(),
                Substitute.For<IRuleEvaluator>(), new ConditionEvaluator(), Substitute.For<IActionExecutorDispatcher>(),
                signingGuard: Guard(user))
            .Handle(new ApproveStepCommand(instanceId, null), default);

    private Task<Result> Reject(Guid instanceId, ICurrentUser user)
        => new RejectStepHandler(_factory.CreateContext(), user, new ConditionEvaluator(),
                Substitute.For<IActionExecutorDispatcher>(), Guard(user))
            .Handle(new RejectStepCommand(instanceId, null), default);

    private async Task<IReadOnlyList<MyStepDto>> QueueAsync(ICurrentUser user)
        => (await new ListMyStepsHandler(_factory.CreateContext(), user, Guard(user))
            .Handle(new ListMyStepsQuery(), default)).Value!;

    private async Task<Guid> SeedAsync()
    {
        var template = WorkflowTemplate.Create(_tenantId, "Opportunity", $"Crédit {Guid.NewGuid():N}", Guid.NewGuid());
        template.AddStep(1, "Avis de l'analyste", approverRoleCode: Analyst);
        template.AddStep(2, "Décision", approverRoleCode: Manager);
        template.Activate();
        await using (var seed = _factory.CreateContext())
        {
            seed.WorkflowTemplates.Add(template);
            seed.WorkflowStepDefinitions.AddRange(template.Steps);
            seed.WorkflowTransitions.AddRange(template.Transitions);
            await seed.SaveChangesAsync();
        }

        var instance = WorkflowInstance.Start(template, _projectId, Guid.NewGuid());
        await using (var seed = _factory.CreateContext())
        {
            seed.WorkflowInstances.Add(instance);
            seed.WorkflowInstanceSteps.AddRange(instance.Steps);
            await seed.SaveChangesAsync();
        }
        return instance.Id;
    }

    private async Task AssignCurrentStepAsync(Guid instanceId, Guid userId)
    {
        await using var db = _factory.CreateContext();
        var step = await db.WorkflowInstanceSteps.AsTracking()
            .SingleAsync(s => s.InstanceId == instanceId && s.Status == StepStatus.AwaitingApproval);
        step.Assign(userId);
        await db.SaveChangesAsync();
    }

    private async Task<int> CurrentOrderAsync(Guid instanceId)
    {
        await using var db = _factory.CreateContext();
        return (await db.WorkflowInstances.SingleAsync(i => i.Id == instanceId)).CurrentStepOrder;
    }
}
