namespace Sankore.Modules.Workflow.Tests.Features.Instances;

using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using NSubstitute;
using Sankore.Modules.Administration.PublicApi;
using Sankore.Modules.Customers.PublicApi;
using Sankore.Modules.Kyc.PublicApi;
using Sankore.Modules.Leads.PublicApi;
using Sankore.Modules.Notifications.PublicApi;
using Sankore.Modules.Workflow.Domain;
using Sankore.Modules.Workflow.Features.Instances.AssignStep;
using Sankore.Modules.Workflow.Features.Instances.CancelInstance;
using Sankore.Modules.Workflow.Features.Instances.RemindStep;
using Sankore.Modules.Workflow.Infrastructure.Notifications;
using Sankore.Modules.Workflow.Tests.TestSupport;
using Sankore.Shared.Infrastructure.Auth;
using Sankore.Shared.Kernel;
using Xunit;

/// <summary>
/// The actions of « Santé des workflows » (2026-11-03): remind whoever must act, hand the step to a
/// named person who is told, cancel with a reason — each leaves a readable line in the history.
/// </summary>
public sealed class HealthActionsTests : IDisposable
{
    private readonly Guid _tenantId = Guid.NewGuid();
    private readonly Guid _leadId = Guid.NewGuid();
    private readonly Guid _agency = Guid.NewGuid();
    private readonly Guid _me = Guid.NewGuid();
    private readonly Guid _manager = Guid.NewGuid();
    private readonly TestWorkflowDbContextFactory _factory;
    private readonly IAdministrationModule _administration = Substitute.For<IAdministrationModule>();
    private readonly INotificationsModule _notifications = Substitute.For<INotificationsModule>();
    private readonly ILeadsModule _leads = Substitute.For<ILeadsModule>();
    private readonly ICurrentUser _currentUser = Substitute.For<ICurrentUser>();

    public HealthActionsTests()
    {
        _factory = new(_tenantId);
        _currentUser.Id.Returns(_me);
        _leads.GetNotificationFactsAsync(_tenantId, "Lead", _leadId, Arg.Any<CancellationToken>())
            .Returns(new LeadNotificationFacts(_agency, null, "Awa", "Konan", null, $"/leads/{_leadId}"));
        _administration.ListActiveUsersInRolesAsync(_tenantId, Arg.Any<IReadOnlyCollection<string>>(), _agency, Arg.Any<CancellationToken>())
            .Returns([_manager, _me]);
        _administration.GetUserNamesAsync(default, default!, default).ReturnsForAnyArgs(
            ci => ((IReadOnlyCollection<Guid>)ci[1]).ToDictionary(id => id, _ => "Moussa Chef") as IReadOnlyDictionary<Guid, string>);
        _notifications.NotifyInAppAsync(default!, default).ReturnsForAnyArgs(Result.Ok(Guid.NewGuid()));
    }

    public void Dispose() => _factory.Dispose();

    [Fact]
    public async Task Remind_tells_the_role_holders_of_the_record_agency_but_not_the_caller()
    {
        var (instanceId, stepId) = await SeedAsync();

        var result = await new RemindStepHandler(_factory.CreateContext(), _currentUser,
                new RecordFactsReader(_leads, Substitute.For<ICustomersModule>(), Substitute.For<IKycModule>()),
                _administration, _notifications, TimeProvider.System)
            .Handle(new RemindStepCommand(instanceId, stepId), default);

        result.Value.Reminded.Should().Be(1);
        await _notifications.Received(1).NotifyInAppAsync(
            Arg.Is<InAppNotificationRequest>(n => n.UserId == _manager && n.Body!.Contains("Awa Konan")), Arg.Any<CancellationToken>());
        (await HistoryAsync(instanceId)).Should().Contain(("REMINDED", "Relance envoyée à Moussa Chef."));
    }

    [Fact]
    public async Task A_second_reminder_within_the_hour_tells_nobody_and_says_so()
    {
        var (instanceId, stepId) = await SeedAsync();
        _notifications.HasInAppNotificationAsync(_tenantId, _manager, Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(true);

        var result = await new RemindStepHandler(_factory.CreateContext(), _currentUser,
                new RecordFactsReader(_leads, Substitute.For<ICustomersModule>(), Substitute.For<IKycModule>()),
                _administration, _notifications, TimeProvider.System)
            .Handle(new RemindStepCommand(instanceId, stepId), default);

        result.Value.Should().Be(new RemindStepResult(0, 1));
        await _notifications.DidNotReceiveWithAnyArgs().NotifyInAppAsync(default!, default);
        (await HistoryAsync(instanceId)).Should().NotContain(h => h.Item1 == "REMINDED");
    }

    [Fact]
    public async Task A_step_handed_over_names_its_new_holder_who_is_told()
    {
        var (instanceId, stepId) = await SeedAsync();

        (await new AssignStepHandler(_factory.CreateContext(), _currentUser, _administration, _notifications)
            .Handle(new AssignStepCommand(instanceId, stepId, _manager, "congés de l'agent"), default)).IsSuccess.Should().BeTrue();

        (await HistoryAsync(instanceId)).Should().Contain(("ASSIGN", "Étape confiée à Moussa Chef : congés de l'agent"));
        await _notifications.Received(1).NotifyInAppAsync(
            Arg.Is<InAppNotificationRequest>(n => n.UserId == _manager), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_cancelled_circuit_says_why_in_its_history()
    {
        var (instanceId, _) = await SeedAsync();

        (await new CancelInstanceHandler(_factory.CreateContext(), _currentUser)
            .Handle(new CancelInstanceCommand(instanceId, "doublon du dossier"), default)).IsSuccess.Should().BeTrue();

        (await HistoryAsync(instanceId)).Should().Contain(("CANCEL", "Circuit annulé : doublon du dossier"));
    }

    private async Task<List<(string, string?)>> HistoryAsync(Guid instanceId)
    {
        await using var db = _factory.CreateContext();
        return (await db.WorkflowAuditEntries.Where(a => a.InstanceId == instanceId).ToListAsync())
            .Select(a => (a.EventCode, a.Comment)).ToList();
    }

    private async Task<(Guid Instance, Guid Step)> SeedAsync()
    {
        var template = WorkflowTemplate.Create(_tenantId, "Lead", "Dossier crédit", Guid.NewGuid());
        template.AddStep(1, "Visite terrain", approverRoleCode: "BranchManager", timeoutHours: 24);
        template.AddStep(2, "Comité", approverRoleCode: "BranchManager");
        template.Activate();
        var instance = WorkflowInstance.Start(template, _leadId, Guid.NewGuid());
        await using var db = _factory.CreateContext();
        db.WorkflowTemplates.Add(template);
        db.WorkflowStepDefinitions.AddRange(template.Steps);
        db.WorkflowTransitions.AddRange(template.Transitions);
        db.WorkflowInstances.Add(instance);
        db.WorkflowInstanceSteps.AddRange(instance.Steps);
        await db.SaveChangesAsync();
        return (instance.Id, instance.Steps.Single(s => s.Order == 1).Id);
    }
}
