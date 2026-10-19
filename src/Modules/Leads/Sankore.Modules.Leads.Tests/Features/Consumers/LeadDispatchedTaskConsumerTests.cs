namespace Sankore.Modules.Leads.Tests.Features.Consumers;

using FluentAssertions;
using MassTransit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Sankore.Modules.Leads.Domain;
using Sankore.Modules.Leads.Features.Consumers;
using Sankore.Modules.Leads.Features.DispatchLead.Events;
using Sankore.Modules.Leads.Features.RecordFirstContact;
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
    public async Task A_reassignment_cancels_the_previous_agents_task_and_gives_the_new_one_theirs()
    {
        var lead = await AddLeadAsync();

        await DispatchAsync(lead.Id, _awa, DateTimeOffset.UtcNow.AddHours(4));
        await DispatchAsync(lead.Id, _koffi, DateTimeOffset.UtcNow.AddHours(4));

        var tasks = await TasksAsync(lead.Id);
        tasks.Single(t => t.AssignedAgentId == _awa).Status.Should().Be(CrmTaskStatus.Cancelled);
        tasks.Single(t => t.AssignedAgentId == _koffi).Status.Should().Be(CrmTaskStatus.Pending);
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

    // ── Harness ─────────────────────────────────────────────────────────────

    private async Task<Lead> AddLeadAsync()
    {
        var lead = LeadTestBuilder.Create().WithTenant(_tenantId).Build();
        await using var db = _db.CreateContext();
        db.Leads.Add(lead);
        await db.SaveChangesAsync();
        return lead;
    }

    private async Task DispatchAsync(Guid leadId, Guid agentId, DateTimeOffset slaDeadline)
    {
        var context = Substitute.For<ConsumeContext<LeadDispatchedEvent>>();
        context.Message.Returns(new LeadDispatchedEvent(leadId, _tenantId, agentId, DispatchingStrategy.RoundRobin, 50, slaDeadline));
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
