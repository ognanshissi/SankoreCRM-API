namespace Sankore.Modules.Leads.Tests.Features.CloseLead;

using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using NSubstitute;
using Sankore.Modules.Leads.Domain;
using Sankore.Modules.Leads.Features.Bulk.BulkClose;
using Sankore.Modules.Leads.Features.CloseLead;
using Sankore.Modules.Leads.Tests.TestSupport;
using Sankore.Modules.Workflow.PublicApi;
using Sankore.Shared.Infrastructure.Messaging;
using Xunit;

/// <summary>
/// An archived lead keeps no open task (product owner, 2026-10-31): they used to stay in the
/// agents' « Ma journée » for a lead nobody works. Lost and disqualified leads keep theirs, and so
/// do the tasks about the lead's project or client.
/// </summary>
public sealed class ArchivedLeadTasksTests : IDisposable
{
    private readonly Guid _tenant = Guid.NewGuid();
    private readonly Guid _agent = Guid.NewGuid();
    private readonly TestDbContextFactory _factory;
    private readonly IEventPublisher _publisher = Substitute.For<IEventPublisher>();

    public ArchivedLeadTasksTests() => _factory = new TestDbContextFactory(_tenant);

    public void Dispose() => _factory.Dispose();

    private async Task<(Lead Lead, CrmTask Open, CrmTask Done)> SeedAsync()
    {
        await using var db = _factory.CreateContext();
        var lead = LeadTestBuilder.Create().WithTenant(_tenant).Build();
        var open = CrmTask.Create(_tenant, CrmTaskType.FirstContact, CrmTaskPriority.High, "Premier contact",
            DateTimeOffset.UtcNow.AddHours(2), leadId: lead.Id, assignedAgentId: _agent);
        var done = CrmTask.Create(_tenant, CrmTaskType.Generic, CrmTaskPriority.Medium, "Déjà fait",
            DateTimeOffset.UtcNow.AddHours(-2), leadId: lead.Id, assignedAgentId: _agent);
        done.Complete();
        db.Leads.Add(lead);
        db.CrmTasks.AddRange(open, done);
        await db.SaveChangesAsync();
        return (lead, open, done);
    }

    private async Task<CrmTaskStatus> StatusOf(Guid taskId)
    {
        await using var db = _factory.CreateContext();
        return (await db.CrmTasks.SingleAsync(t => t.Id == taskId)).Status;
    }

    [Fact]
    public async Task Archiving_a_lead_cancels_its_open_tasks_and_leaves_the_done_ones()
    {
        var (lead, open, done) = await SeedAsync();

        await using (var db = _factory.CreateContext())
            (await new CloseLeadHandler(db, _publisher).Handle(new CloseLeadCommand(lead.Id, LeadCloseReason.Archived), CancellationToken.None))
                .IsSuccess.Should().BeTrue();

        (await StatusOf(open.Id)).Should().Be(CrmTaskStatus.Cancelled);
        (await StatusOf(done.Id)).Should().Be(CrmTaskStatus.Completed);
    }

    [Theory]
    [InlineData(LeadCloseReason.Lost)]
    [InlineData(LeadCloseReason.Disqualified)]
    public async Task A_lost_or_disqualified_lead_keeps_its_tasks(LeadCloseReason reason)
    {
        var (lead, open, _) = await SeedAsync();

        await using (var db = _factory.CreateContext())
            await new CloseLeadHandler(db, _publisher).Handle(new CloseLeadCommand(lead.Id, reason), CancellationToken.None);

        (await StatusOf(open.Id)).Should().Be(CrmTaskStatus.Pending);
    }

    [Fact]
    public async Task A_workflow_steps_task_tells_the_step_it_was_cancelled()
    {
        var (lead, _, _) = await SeedAsync();
        var stepId = Guid.NewGuid();
        await using (var db = _factory.CreateContext())
        {
            db.CrmTasks.Add(CrmTask.Create(_tenant, CrmTaskType.Generic, CrmTaskPriority.Medium, "Étape du circuit",
                DateTimeOffset.UtcNow.AddDays(1), leadId: lead.Id, assignedAgentId: _agent,
                triggerEventType: WorkflowTaskRequested.TriggerType, triggerEventId: stepId));
            await db.SaveChangesAsync();
        }

        await using (var db = _factory.CreateContext())
            await new CloseLeadHandler(db, _publisher).Handle(new CloseLeadCommand(lead.Id, LeadCloseReason.Archived), CancellationToken.None);

        await _publisher.Received(1).PublishAsync(
            Arg.Is<WorkflowStepTaskClosed>(e => e.InstanceStepId == stepId), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_bulk_archive_cancels_the_tasks_of_the_leads_it_archived()
    {
        var (lead, open, _) = await SeedAsync();

        await using (var db = _factory.CreateContext())
            (await new BulkCloseHandler(db, _publisher).Handle(
                new BulkCloseCommand([lead.Id, Guid.NewGuid()], LeadCloseReason.Archived, null), CancellationToken.None))
                .Value.Succeeded.Should().Be(1);

        (await StatusOf(open.Id)).Should().Be(CrmTaskStatus.Cancelled);
    }
}
