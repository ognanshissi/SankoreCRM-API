namespace Sankore.Modules.Leads.Tests.Features.MergeLeads;

using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Leads.Domain;
using Sankore.Modules.Leads.Features.Tasks;
using Sankore.Modules.Leads.Tests.TestSupport;
using Xunit;

/// <summary>
/// The open tasks of a merged duplicate follow the person to the lead that is kept (product owner,
/// 2026-11-05). Tested on the helper: the merge handler's ExecuteUpdate calls do not run on InMemory.
/// </summary>
public sealed class MergedLeadTasksTests : IDisposable
{
    private readonly Guid _tenant = Guid.NewGuid();
    private readonly Guid _sourceAgent = Guid.NewGuid();
    private readonly Guid _targetAgent = Guid.NewGuid();
    private readonly Guid _actor = Guid.NewGuid();
    private readonly TestDbContextFactory _factory;

    public MergedLeadTasksTests() => _factory = new TestDbContextFactory(_tenant);

    public void Dispose() => _factory.Dispose();

    private async Task<(Guid Source, Guid Target)> SeedLeadsAsync(Guid? targetOwner = null, bool contacted = false)
    {
        await using var db = _factory.CreateContext();
        var source = LeadTestBuilder.Create().WithTenant(_tenant).Fresh().Build();
        var target = LeadTestBuilder.Create().WithTenant(_tenant).Fresh().Build();
        if (targetOwner is { } owner) target.SetOwner(owner);
        if (contacted) target.RecordContactEstablished();
        db.Leads.AddRange(source, target);
        await db.SaveChangesAsync();
        return (source.Id, target.Id);
    }

    private async Task<Guid> AddTaskAsync(
        Guid leadId, string type = CrmTaskType.Generic, string? trigger = null, Guid? agent = null, bool done = false)
    {
        await using var db = _factory.CreateContext();
        var task = CrmTask.Create(_tenant, type, CrmTaskPriority.Medium, "Tâche",
            DateTimeOffset.UtcNow.AddHours(2), leadId: leadId, assignedAgentId: agent ?? _sourceAgent,
            slaDeadline: DateTimeOffset.UtcNow.AddHours(4), triggerEventType: trigger, triggerEventId: trigger is null ? null : leadId);
        if (done) task.Complete();
        db.CrmTasks.Add(task);
        await db.SaveChangesAsync();
        return task.Id;
    }

    private async Task<IReadOnlyCollection<Guid>> MergeAsync(Guid sourceId, Guid targetId)
    {
        await using var db = _factory.CreateContext();
        var source = await db.Leads.SingleAsync(l => l.Id == sourceId);
        var target = await db.Leads.SingleAsync(l => l.Id == targetId);
        var agents = await MergedLeadTasks.MoveOpenAsync(db, source, target, _actor, DateTimeOffset.UtcNow, CancellationToken.None);
        await db.SaveChangesAsync();
        return agents;
    }

    private async Task<CrmTask> TaskAsync(Guid id)
    {
        await using var db = _factory.CreateContext();
        return await db.CrmTasks.SingleAsync(t => t.Id == id);
    }

    [Fact]
    public async Task An_open_task_moves_to_the_kept_lead_and_its_holder_with_a_history_line_and_the_same_deadlines()
    {
        var (source, target) = await SeedLeadsAsync(targetOwner: _targetAgent);
        var id = await AddTaskAsync(source);
        var before = await TaskAsync(id);

        var agents = await MergeAsync(source, target);

        var task = await TaskAsync(id);
        task.LeadId.Should().Be(target);
        task.Status.Should().Be(CrmTaskStatus.Pending);
        task.AssignedAgentId.Should().Be(_targetAgent);
        task.DueAt.Should().Be(before.DueAt);
        task.SlaDeadline.Should().Be(before.SlaDeadline);
        agents.Should().BeEquivalentTo([_sourceAgent, _targetAgent]);

        await using var db = _factory.CreateContext();
        var line = await db.TaskReassignments.SingleAsync(r => r.TaskId == id);
        line.PreviousAgentId.Should().Be(_sourceAgent);
        line.NewAgentId.Should().Be(_targetAgent);
    }

    [Fact]
    public async Task A_kept_lead_nobody_holds_leaves_the_task_with_its_agent()
    {
        var (source, target) = await SeedLeadsAsync();
        var id = await AddTaskAsync(source);

        await MergeAsync(source, target);

        var task = await TaskAsync(id);
        task.LeadId.Should().Be(target);
        task.AssignedAgentId.Should().Be(_sourceAgent);
    }

    [Fact]
    public async Task A_done_task_stays_on_the_duplicate()
    {
        var (source, target) = await SeedLeadsAsync();
        var id = await AddTaskAsync(source, done: true);

        await MergeAsync(source, target);

        (await TaskAsync(id)).LeadId.Should().Be(source);
    }

    [Fact]
    public async Task An_assignment_task_the_kept_lead_already_has_is_cancelled_not_doubled()
    {
        var (source, target) = await SeedLeadsAsync(targetOwner: _targetAgent);
        var duplicate = await AddTaskAsync(source, CrmTaskType.FirstContact, TaskTriggerEvents.LeadDispatched);
        var kept = await AddTaskAsync(target, CrmTaskType.FirstContact, TaskTriggerEvents.LeadDispatched, _targetAgent);

        await MergeAsync(source, target);

        (await TaskAsync(duplicate)).Status.Should().Be(CrmTaskStatus.Cancelled);
        (await TaskAsync(kept)).Status.Should().Be(CrmTaskStatus.Pending);
    }

    [Fact]
    public async Task A_first_contact_task_is_cancelled_when_the_kept_lead_was_already_contacted()
    {
        var (source, target) = await SeedLeadsAsync(contacted: true);
        var id = await AddTaskAsync(source, CrmTaskType.FirstContact, TaskTriggerEvents.LeadDispatched);

        await MergeAsync(source, target);

        (await TaskAsync(id)).Status.Should().Be(CrmTaskStatus.Cancelled);
    }

    [Fact]
    public async Task A_follow_up_of_the_same_type_is_not_a_duplicate()
    {
        var (source, target) = await SeedLeadsAsync();
        var moved = await AddTaskAsync(source);
        await AddTaskAsync(target);

        await MergeAsync(source, target);

        (await TaskAsync(moved)).Status.Should().Be(CrmTaskStatus.Pending);
        (await TaskAsync(moved)).LeadId.Should().Be(target);
    }
}
