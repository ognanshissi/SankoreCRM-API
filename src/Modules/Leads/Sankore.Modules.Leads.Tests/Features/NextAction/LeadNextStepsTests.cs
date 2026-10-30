namespace Sankore.Modules.Leads.Tests.Features.NextAction;

using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Leads.Domain;
using Sankore.Modules.Leads.Features.NextAction;
using Sankore.Modules.Leads.Tests.TestSupport;
using Xunit;

/// <summary>
/// A lead's next step is its earliest open commitment, with that commitment's own date — and only
/// when nothing is planned, the engine's suggestion with no date at all.
/// </summary>
public sealed class LeadNextStepsTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 10, 20, 9, 0, 0, TimeSpan.Zero);

    private readonly Guid _tenantId = Guid.NewGuid();
    private readonly Guid _agent = Guid.NewGuid();
    private readonly TestDbContextFactory _db;

    public LeadNextStepsTests() => _db = new TestDbContextFactory(_tenantId);

    public void Dispose() => _db.Dispose();

    [Fact]
    public async Task The_earliest_commitment_wins_whatever_its_kind()
    {
        var lead = Facts();
        await AddTaskAsync(lead.Id, "Envoyer la simulation", Now.AddDays(3));
        await AddReminderAsync(lead.Id, "Rappeler après la paie", Now.AddDays(1));

        var step = await NextStepOf(lead);

        step.Kind.Should().Be(NextStepKind.Reminder);
        step.Title.Should().Be("Rappeler après la paie");
        step.DueAt.Should().Be(Now.AddDays(1));
        step.IsOverdue.Should().BeFalse();
    }

    [Fact]
    public async Task A_commitment_past_its_date_is_overdue()
    {
        var lead = Facts();
        await AddTaskAsync(lead.Id, "Relancer", Now.AddHours(-3));

        var step = await NextStepOf(lead);

        step.Kind.Should().Be(NextStepKind.Task);
        step.IsOverdue.Should().BeTrue();
        step.AssigneeId.Should().Be(_agent);
    }

    [Fact]
    public async Task Closed_tasks_and_resolved_reminders_are_not_commitments()
    {
        var lead = Facts(score: 0, status: LeadStatus.New);
        var task = await AddTaskAsync(lead.Id, "Déjà fait", Now.AddDays(1));
        await using (var db = _db.CreateContext())
        {
            // The context reads untracked by default: without AsTracking the completion is never saved.
            var tracked = await db.CrmTasks.AsTracking().FirstAsync(t => t.Id == task.Id);
            tracked.Complete();
            await db.SaveChangesAsync();
        }

        var step = await NextStepOf(lead);

        step.Kind.Should().Be(NextStepKind.Suggestion);
    }

    [Fact]
    public async Task An_owed_first_contact_is_due_at_its_sla_deadline()
    {
        var assignmentId = await AddAssignmentAsync(out var leadId, sla: Now.AddHours(20));
        var lead = Facts(leadId, assignmentId: assignmentId);

        var step = await NextStepOf(lead);

        step.Kind.Should().Be(NextStepKind.FirstContact);
        step.DueAt.Should().Be(Now.AddHours(20));
        step.AssigneeId.Should().Be(_agent);
    }

    [Fact]
    public async Task The_first_contact_task_stands_for_the_sla_rather_than_appearing_twice()
    {
        var assignmentId = await AddAssignmentAsync(out var leadId, sla: Now.AddHours(20));
        await AddTaskAsync(leadId, "Premier contact avec Didier", Now.AddHours(20), CrmTaskType.FirstContact);
        var lead = Facts(leadId, assignmentId: assignmentId);

        var step = await NextStepOf(lead);

        step.Kind.Should().Be(NextStepKind.Task);
        step.Title.Should().Be("Premier contact avec Didier");
    }

    [Fact]
    public async Task With_nothing_planned_the_suggestion_carries_no_date()
    {
        var lead = Facts(score: 0, status: LeadStatus.New);

        var step = await NextStepOf(lead);

        step.Kind.Should().Be(NextStepKind.Suggestion);
        step.ActionType.Should().Be(NextActionType.Qualify);
        step.DueAt.Should().BeNull("a date computed from now moves forward on every read and can never be late");
        step.IsOverdue.Should().BeFalse();
    }

    [Fact]
    public async Task A_closed_lead_has_no_next_step()
    {
        var lead = Facts(status: LeadStatus.Converted);
        await AddTaskAsync(lead.Id, "Oubliée", Now.AddDays(1));

        await using var db = _db.CreateContext();
        var steps = await LeadNextSteps.ForLeadsAsync(db, [lead], Now, default);

        steps.Should().NotContainKey(lead.Id);
    }

    [Fact]
    public async Task Each_lead_of_a_page_gets_its_own_step()
    {
        var planned = Facts();
        var unplanned = Facts(score: 0, status: LeadStatus.New);
        await AddTaskAsync(planned.Id, "Visite en agence", Now.AddDays(2));

        await using var db = _db.CreateContext();
        var steps = await LeadNextSteps.ForLeadsAsync(db, [planned, unplanned], Now, default);

        steps[planned.Id].Kind.Should().Be(NextStepKind.Task);
        steps[unplanned.Id].Kind.Should().Be(NextStepKind.Suggestion);
    }

    private LeadFacts Facts(
        Guid? id = null, int score = 50, LeadStatus status = LeadStatus.Open, Guid? assignmentId = null) =>
        new(id ?? Guid.NewGuid(), status, score, 0, Now.AddDays(-1), assignmentId, _agent);

    private async Task<LeadNextStepDto> NextStepOf(LeadFacts lead)
    {
        await using var db = _db.CreateContext();
        var steps = await LeadNextSteps.ForLeadsAsync(db, [lead], Now, default);
        return steps[lead.Id];
    }

    private async Task<CrmTask> AddTaskAsync(
        Guid leadId, string title, DateTimeOffset dueAt, string type = CrmTaskType.Generic)
    {
        await using var db = _db.CreateContext();
        var task = CrmTask.Create(_tenantId, type, CrmTaskPriority.Medium, title, dueAt,
            leadId: leadId, assignedAgentId: _agent);
        db.CrmTasks.Add(task);
        await db.SaveChangesAsync();
        return task;
    }

    private async Task AddReminderAsync(Guid leadId, string title, DateTimeOffset dueAt)
    {
        await using var db = _db.CreateContext();
        db.LeadReminders.Add(LeadReminder.Create(_tenantId, leadId, title, _agent, dueAt));
        await db.SaveChangesAsync();
    }

    private Task<Guid> AddAssignmentAsync(out Guid leadId, DateTimeOffset sla)
    {
        leadId = Guid.NewGuid();
        var assignment = LeadAssignment.Create(
            _tenantId, leadId, _agent, DispatchingStrategy.RoundRobin, 0, sla, Now.AddHours(-4));
        return SaveAsync(assignment);

        async Task<Guid> SaveAsync(LeadAssignment a)
        {
            await using var db = _db.CreateContext();
            db.LeadAssignments.Add(a);
            await db.SaveChangesAsync();
            return a.Id;
        }
    }
}
