namespace Sankore.Modules.Leads.Tests.Features.Consumers;

using FluentAssertions;
using MassTransit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Sankore.Modules.Leads.Domain;
using Sankore.Modules.Leads.Features.Consumers;
using Sankore.Modules.Leads.Features.DispatchLead.Events;
using Sankore.Modules.Leads.Features.DispatchLead;
using Sankore.Modules.Leads.Features.RecordFirstContact;
using Sankore.Modules.Leads.Features.Tasks.CompleteTask;
using Sankore.Shared.Infrastructure.Auth;
using Sankore.Modules.Leads.Tests.TestSupport;
using Xunit;

/// <summary>
/// Every assignment must put a task on the agent's list, once, and take it off the previous
/// agent's. Before, a tenant with no generation rule — the default — got no task at all.
/// </summary>
public sealed class LeadDispatchedTaskConsumerTests : IDisposable
{
    private readonly Guid _tenantId = Guid.NewGuid();
    private readonly Guid _awa = Guid.NewGuid();
    private readonly Guid _koffi = Guid.NewGuid();
    private readonly TestDbContextFactory _db;

    public LeadDispatchedTaskConsumerTests() => _db = new TestDbContextFactory(_tenantId);

    public void Dispose() => _db.Dispose();

    [Fact]
    public async Task Without_any_rule_the_agent_gets_a_first_contact_task_due_at_the_sla_deadline()
    {
        var lead = await AddLeadAsync();
        var deadline = DateTimeOffset.UtcNow.AddHours(4);

        await DispatchAsync(lead.Id, _awa, deadline);

        var task = (await TasksAsync(lead.Id)).Should().ContainSingle().Subject;
        task.Type.Should().Be(CrmTaskType.FirstContact);
        task.AssignedAgentId.Should().Be(_awa);
        task.Status.Should().Be(CrmTaskStatus.Pending);
        task.DueAt.Should().Be(deadline);
        task.Title.Should().Contain(lead.FullName);
    }

    [Fact]
    public async Task A_redelivered_dispatch_does_not_stack_a_second_task()
    {
        var lead = await AddLeadAsync();
        var deadline = DateTimeOffset.UtcNow.AddHours(4);

        await DispatchAsync(lead.Id, _awa, deadline);
        await DispatchAsync(lead.Id, _awa, deadline);

        (await TasksAsync(lead.Id)).Should().ContainSingle();
    }

    [Fact]
    public async Task A_reassignment_transfers_the_task_to_the_new_agent_instead_of_replacing_it()
    {
        // It used to cancel Awa's task and create a second one for Koffi: the same work split over
        // two rows, with nothing saying who had held it before.
        var lead = await AddLeadAsync();
        var first = DateTimeOffset.UtcNow.AddHours(4);
        var second = DateTimeOffset.UtcNow.AddHours(6);

        await DispatchAsync(lead.Id, _awa, first);
        await DispatchAsync(lead.Id, _koffi, second);

        var task = (await TasksAsync(lead.Id)).Should().ContainSingle().Subject;
        task.AssignedAgentId.Should().Be(_koffi);
        task.Status.Should().Be(CrmTaskStatus.Pending);
        task.SlaDeadline.Should().Be(second);
        task.DueAt.Should().Be(second);

        await using var db = _db.CreateContext();
        var step = (await db.TaskReassignments.Where(r => r.TaskId == task.Id).ToListAsync())
            .Should().ContainSingle().Subject;
        step.PreviousAgentId.Should().Be(_awa);
        step.NewAgentId.Should().Be(_koffi);
        step.PreviousSlaDeadline.Should().Be(first);
        step.NewSlaDeadline.Should().Be(second);
    }

    [Fact]
    public async Task A_manual_reassignment_records_who_moved_the_task_and_why()
    {
        var lead = await AddLeadAsync();
        var manager = Guid.NewGuid();

        await DispatchAsync(lead.Id, _awa, DateTimeOffset.UtcNow.AddHours(4));
        await DispatchAsync(lead.Id, _koffi, DateTimeOffset.UtcNow.AddHours(4), manager, "Awa en congé");

        await using var db = _db.CreateContext();
        var step = await db.TaskReassignments.SingleAsync();
        step.ActorId.Should().Be(manager);
        step.Reason.Should().Be("Lead réattribué — Awa en congé");
    }

    [Fact]
    public async Task The_attribution_history_starts_at_the_creation_and_lists_every_change()
    {
        var lead = await AddLeadAsync();
        var first = DateTimeOffset.UtcNow.AddHours(4);
        var second = DateTimeOffset.UtcNow.AddHours(6);

        await DispatchAsync(lead.Id, _awa, first);
        await DispatchAsync(lead.Id, _koffi, second);
        var task = (await TasksAsync(lead.Id)).Single();

        await using var db = _db.CreateContext();
        var result = await new Sankore.Modules.Leads.Features.Tasks.GetTaskAssignments.GetTaskAssignmentsHandler(db, TestTaskAccess.Administrator(db))
            .Handle(new Sankore.Modules.Leads.Features.Tasks.GetTaskAssignments.GetTaskAssignmentsQuery(task.Id), default);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().HaveCount(2);
        result.Value[0].Kind.Should().Be("Initial");
        result.Value[0].AgentId.Should().Be(_awa);
        result.Value[0].SlaDeadline.Should().Be(first);
        result.Value[1].Kind.Should().Be("Reassigned");
        result.Value[1].PreviousAgentId.Should().Be(_awa);
        result.Value[1].AgentId.Should().Be(_koffi);
        result.Value[1].SlaDeadline.Should().Be(second);
    }

    [Fact]
    public async Task Configured_rules_replace_the_default_task()
    {
        var lead = await AddLeadAsync();
        await using (var db = _db.CreateContext())
        {
            db.TaskGenerationRules.Add(TaskGenerationRule.Create(
                _tenantId, TaskTriggerEvents.LeadDispatched, CrmTaskType.Qualification, CrmTaskPriority.Medium,
                "Qualifier {LeadName}", slaDuration: TimeSpan.FromHours(24), dueDuration: TimeSpan.FromHours(24)));
            await db.SaveChangesAsync();
        }

        await DispatchAsync(lead.Id, _awa, DateTimeOffset.UtcNow.AddHours(4));

        var task = (await TasksAsync(lead.Id)).Should().ContainSingle().Subject;
        task.Type.Should().Be(CrmTaskType.Qualification);
        task.Title.Should().Be($"Qualifier {lead.FullName}");
    }

    [Fact]
    public async Task Recording_the_first_contact_completes_the_task()
    {
        var lead = await AddLeadAsync();
        var deadline = DateTimeOffset.UtcNow.AddHours(4);

        var assignment = LeadAssignment.Create(_tenantId, lead.Id, _awa, DispatchingStrategy.RoundRobin, 50,
            slaDeadline: deadline, createdAt: DateTimeOffset.UtcNow);
        await using (var db = _db.CreateContext())
        {
            var tracked = await db.Leads.AsTracking().FirstAsync(l => l.Id == lead.Id);
            tracked.AssignTo(assignment, currentAssignment: null).IsSuccess.Should().BeTrue();
            tracked.ClearDomainEvents();
            db.LeadAssignments.Add(assignment);
            await db.SaveChangesAsync();
        }
        await DispatchAsync(lead.Id, _awa, deadline);

        await using (var db = _db.CreateContext())
        {
            var result = await new RecordFirstContactHandler(db).Handle(new RecordFirstContactCommand(lead.Id), default);
            result.IsSuccess.Should().BeTrue();
        }

        (await TasksAsync(lead.Id)).Single().Status.Should().Be(CrmTaskStatus.Completed);
    }

    [Fact]
    public async Task Completing_the_first_contact_task_records_the_first_contact()
    {
        var lead = await AddLeadAsync();
        var deadline = DateTimeOffset.UtcNow.AddHours(4);
        var assignment = await AssignAsync(lead.Id, deadline);
        await DispatchAsync(lead.Id, _awa, deadline);
        var task = (await TasksAsync(lead.Id)).Single();

        var user = Substitute.For<ICurrentUser>();
        user.Id.Returns(_awa);
        user.Roles.Returns(Array.Empty<string>());
        await using (var db = _db.CreateContext())
        {
            var result = await new CompleteTaskHandler(db, new AgentCapacityService(db), Substitute.For<IBus>(), user, new Sankore.Shared.Infrastructure.Outbox.OutboxEventPublisher<Sankore.Modules.Leads.Infrastructure.LeadsDbContext>(db))
                .Handle(new CompleteTaskCommand(task.Id), default);
            result.IsSuccess.Should().BeTrue();
        }

        // Before, the assignment still owed its first contact: the lead's next step came back as
        // « Premier contact » the moment the agent ticked the task off.
        await using (var db = _db.CreateContext())
        {
            var stored = await db.LeadAssignments.SingleAsync(a => a.Id == assignment.Id);
            stored.FirstContactAt.Should().NotBeNull();
            stored.FirstContactSource.Should().Be(FirstContactSource.AgentDeclared);
            var storedLead = await db.Leads.SingleAsync(l => l.Id == lead.Id);
            storedLead.PipelineStage.Should().Be(PipelineStage.ContactEstablished);
        }
    }

    // ── Harness ─────────────────────────────────────────────────────────────

    private async Task<LeadAssignment> AssignAsync(Guid leadId, DateTimeOffset deadline)
    {
        var assignment = LeadAssignment.Create(_tenantId, leadId, _awa, DispatchingStrategy.RoundRobin, 50,
            slaDeadline: deadline, createdAt: DateTimeOffset.UtcNow);
        await using var db = _db.CreateContext();
        var tracked = await db.Leads.AsTracking().FirstAsync(l => l.Id == leadId);
        tracked.AssignTo(assignment, currentAssignment: null).IsSuccess.Should().BeTrue();
        tracked.ClearDomainEvents();
        db.LeadAssignments.Add(assignment);
        await db.SaveChangesAsync();
        return assignment;
    }

    private async Task<Lead> AddLeadAsync()
    {
        var lead = LeadTestBuilder.Create().WithTenant(_tenantId).Build();
        await using var db = _db.CreateContext();
        db.Leads.Add(lead);
        await db.SaveChangesAsync();
        return lead;
    }

    private async Task DispatchAsync(
        Guid leadId, Guid agentId, DateTimeOffset slaDeadline, Guid? actorId = null, string? reason = null)
    {
        var context = Substitute.For<ConsumeContext<LeadDispatchedEvent>>();
        context.Message.Returns(new LeadDispatchedEvent(
            leadId, _tenantId, agentId, DispatchingStrategy.RoundRobin, 50, slaDeadline, actorId, reason));
        context.CancellationToken.Returns(CancellationToken.None);

        await using var db = _db.CreateContext();
        await new LeadDispatchedTaskConsumer(db, NullLogger<LeadDispatchedTaskConsumer>.Instance).Consume(context);
    }

    private async Task<List<CrmTask>> TasksAsync(Guid leadId)
    {
        await using var db = _db.CreateContext();
        return await db.CrmTasks.Where(t => t.LeadId == leadId).ToListAsync();
    }
}
