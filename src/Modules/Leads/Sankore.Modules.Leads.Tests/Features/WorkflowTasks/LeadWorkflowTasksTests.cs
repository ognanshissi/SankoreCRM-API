namespace Sankore.Modules.Leads.Tests.Features.WorkflowTasks;

using System.Text.Json;
using FluentAssertions;
using MassTransit;
using NSubstitute;
using Sankore.Modules.Administration.PublicApi;
using Sankore.Modules.Customers.PublicApi;
using Sankore.Modules.Leads.Domain;
using Sankore.Modules.Leads.Features.DispatchLead;
using Sankore.Modules.Leads.Features.Tasks.CompleteTask;
using Sankore.Modules.Leads.Features.WorkflowTasks;
using Sankore.Modules.Leads.Infrastructure;
using Sankore.Modules.Leads.Tests.TestSupport;
using Sankore.Modules.Workflow.PublicApi;
using Sankore.Shared.Infrastructure.Auth;
using Sankore.Shared.Infrastructure.Outbox;
using Xunit;

/// <summary>
/// The CRM task of a workflow « Tâche manuelle » step: created about the record for its agent,
/// once; withdrawn when the step closes without it; and completing it tells the workflow.
/// </summary>
public sealed class LeadWorkflowTasksTests : IDisposable
{
    private readonly Guid _tenantId = Guid.NewGuid();
    private readonly Guid _agent = Guid.NewGuid();
    private readonly TestDbContextFactory _factory;
    private readonly IAdministrationModule _administration = Substitute.For<IAdministrationModule>();

    public LeadWorkflowTasksTests()
    {
        _factory = new TestDbContextFactory(_tenantId);
        _administration.GetUserNamesAsync(default, default!, default)
            .ReturnsForAnyArgs(new Dictionary<Guid, string> { [_agent] = "Koffi Yao" });
    }

    public void Dispose() => _factory.Dispose();

    [Fact]
    public async Task A_task_is_created_once_about_the_lead_for_its_agent()
    {
        var lead = await SeedLeadAsync();
        var request = Request("Lead", lead.Id, dueInHours: 8);

        var note = await Service().CreateAsync(request, default);
        var again = await Service().CreateAsync(request, default);

        note.Should().Be("Tâche « Appeler le client » créée pour Koffi Yao.");
        again.Should().BeNull("a redelivered request finds its task");
        await using var db = _factory.CreateContext();
        var task = db.CrmTasks.Should().ContainSingle().Subject;
        task.EntityType.Should().Be(TaskEntityType.Lead);
        task.LeadId.Should().Be(lead.Id);
        task.AssignedAgentId.Should().Be(_agent);
        task.TriggerEventType.Should().Be(WorkflowTaskRequested.TriggerType);
        task.TriggerEventId.Should().Be(request.InstanceStepId);
        task.DueAt.Should().BeCloseTo(DateTimeOffset.UtcNow.AddHours(8), TimeSpan.FromMinutes(1));
    }

    [Fact]
    public async Task A_kyc_file_task_is_created_unassigned_and_says_so()
    {
        var note = await Service().CreateAsync(Request("KycFile", Guid.NewGuid()), default);

        note.Should().Contain("sans agent");
        await using var db = _factory.CreateContext();
        db.CrmTasks.Single().AssignedAgentId.Should().BeNull();
    }

    [Fact]
    public async Task Withdrawing_cancels_the_open_task_only()
    {
        var lead = await SeedLeadAsync();
        var request = Request("Lead", lead.Id);
        await Service().CreateAsync(request, default);

        (await Service().WithdrawAsync(new WorkflowTaskWithdrawn(_tenantId, request.InstanceStepId), default)).Should().BeTrue();
        (await Service().WithdrawAsync(new WorkflowTaskWithdrawn(_tenantId, request.InstanceStepId), default)).Should().BeFalse();

        await using var db = _factory.CreateContext();
        db.CrmTasks.Single().Status.Should().Be(CrmTaskStatus.Cancelled);
    }

    [Fact]
    public async Task Completing_the_task_tells_the_workflow_in_the_same_transaction()
    {
        var lead = await SeedLeadAsync();
        var request = Request("Lead", lead.Id);
        await Service().CreateAsync(request, default);
        Guid taskId;
        await using (var db = _factory.CreateContext()) taskId = db.CrmTasks.Single().Id;

        var user = Substitute.For<ICurrentUser>();
        user.Id.Returns(_agent);
        user.TenantId.Returns(_tenantId);
        user.Roles.Returns(Array.Empty<string>());
        await using (var db = _factory.CreateContext())
        {
            var result = await new CompleteTaskHandler(db, new AgentCapacityService(db), Substitute.For<IBus>(), user,
                    new OutboxEventPublisher<LeadsDbContext>(db))
                .Handle(new CompleteTaskCommand(taskId), default);
            result.IsSuccess.Should().BeTrue(result.Error);
        }

        await using var verify = _factory.CreateContext();
        var row = verify.OutboxMessages.Single(m => m.EventType == typeof(WorkflowStepTaskClosed).AssemblyQualifiedName);
        var closed = JsonSerializer.Deserialize<WorkflowStepTaskClosed>(row.PayloadJson, OutboxJson.Options)!;
        closed.InstanceStepId.Should().Be(request.InstanceStepId);
        closed.Completed.Should().BeTrue();
        closed.ActedByUserId.Should().Be(_agent);
        closed.TaskTitle.Should().Be("Appeler le client");
    }

    private LeadWorkflowTasks Service()
        => new(_factory.CreateContext(), Substitute.For<ICustomersModule>(), _administration, TimeProvider.System);

    private WorkflowTaskRequested Request(string entityType, Guid entityId, int? dueInHours = null)
        => new(_tenantId, Guid.NewGuid(), Guid.NewGuid(), "Appeler le client", entityType, entityId, "agent", dueInHours);

    private async Task<Lead> SeedLeadAsync()
    {
        var lead = LeadTestBuilder.Create().WithTenant(_tenantId).Fresh().Build();
        typeof(Lead).GetProperty(nameof(Lead.OwnerId))!.SetValue(lead, _agent);
        await using var db = _factory.CreateContext();
        db.Leads.Add(lead);
        await db.SaveChangesAsync();
        return lead;
    }
}
