namespace Sankore.Modules.Leads.Tests.Features.Nurturing;

using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Leads.Domain;
using Sankore.Modules.Leads.Features.Bulk.BulkRecycle;
using Sankore.Modules.Leads.Features.NurtureLead;
using Sankore.Modules.Leads.Features.RecycleLead;
using Sankore.Modules.Leads.Tests.TestSupport;
using Xunit;

/// <summary>
/// A lead put in nurturing or recycled owes nobody a first call (product owner, 2026-11-05): it used to
/// keep its open task, its SLA alerts and its escalations while it was set aside.
/// </summary>
public sealed class SetAsideLeadTests : IDisposable
{
    private readonly Guid _tenant = Guid.NewGuid();
    private readonly Guid _agent = Guid.NewGuid();
    private readonly TestDbContextFactory _factory;

    public SetAsideLeadTests() => _factory = new TestDbContextFactory(_tenant);

    public void Dispose() => _factory.Dispose();

    private sealed record Seeded(Guid Lead, Guid Assignment, Guid FirstContact, Guid FollowUp, Guid Sequence);

    private async Task<Seeded> SeedAsync(bool contacted = false, bool lost = false)
    {
        await using var db = _factory.CreateContext();
        var lead = LeadTestBuilder.Create().WithTenant(_tenant).Build();
        lead.ApplyMergeOverrides(email: "awa@example.test", emailBlindIndex: "index");
        var assignment = LeadAssignment.Create(_tenant, lead.Id, _agent, DispatchingStrategy.RoundRobin, 50,
            DateTimeOffset.UtcNow.AddHours(-1), DateTimeOffset.UtcNow.AddDays(-1));
        lead.AssignTo(assignment, currentAssignment: null).IsSuccess.Should().BeTrue();
        if (contacted) assignment.RecordFirstContact(DateTimeOffset.UtcNow, FirstContactSource.AgentDeclared);
        if (lost) lead.Close(LeadCloseReason.Lost, "Plus intéressé");

        var firstContact = CrmTask.Create(_tenant, CrmTaskType.FirstContact, CrmTaskPriority.High, "Premier contact",
            DateTimeOffset.UtcNow.AddHours(4), leadId: lead.Id, assignedAgentId: _agent,
            triggerEventType: TaskTriggerEvents.LeadDispatched, triggerEventId: lead.Id);
        var followUp = CrmTask.Create(_tenant, CrmTaskType.Generic, CrmTaskPriority.Medium, "Rappeler pour le dossier",
            DateTimeOffset.UtcNow.AddDays(3), leadId: lead.Id, assignedAgentId: _agent);
        var sequence = NurturingSequence.Create(_tenant, "Relance");
        sequence.AddStep(0, TimeSpan.Zero, "nurture.a");

        db.Leads.Add(lead);
        db.LeadAssignments.Add(assignment);
        db.CrmTasks.AddRange(firstContact, followUp);
        db.NurturingSequences.Add(sequence);
        db.LeadConsents.Add(LeadConsent.Create(_tenant, lead.Id, ConsentType.Marketing, ConsentChannel.WebForm, Guid.NewGuid(), TimeProvider.System));
        await db.SaveChangesAsync();
        return new Seeded(lead.Id, assignment.Id, firstContact.Id, followUp.Id, sequence.Id);
    }

    private async Task AssertSetAsideAsync(Seeded seeded)
    {
        await using var db = _factory.CreateContext();
        (await db.CrmTasks.SingleAsync(t => t.Id == seeded.FirstContact)).Status.Should().Be(CrmTaskStatus.Cancelled);
        (await db.CrmTasks.SingleAsync(t => t.Id == seeded.FollowUp)).Status.Should().Be(CrmTaskStatus.Pending,
            "a follow-up the agent planned is theirs, not the first call");
        var assignment = await db.LeadAssignments.SingleAsync(a => a.Id == seeded.Assignment);
        assignment.IsSuperseded.Should().BeTrue("the SLA job and the agenda skip a superseded row");
        assignment.FirstContactAt.Should().BeNull("being set aside is not a contact");
        assignment.HasBreachedSla(DateTimeOffset.UtcNow).Should().BeFalse();
        (await db.Leads.SingleAsync(l => l.Id == seeded.Lead)).CurrentAssignedId.Should().Be(_agent,
            "the agent still holds the lead and will find it again");
    }

    [Fact]
    public async Task Placing_a_lead_in_nurturing_stops_the_wait_for_its_first_contact()
    {
        var seeded = await SeedAsync();

        await using (var db = _factory.CreateContext())
            (await new NurtureLeadHandler(db, TimeProvider.System).Handle(new NurtureLeadCommand(seeded.Lead, seeded.Sequence), default))
                .IsSuccess.Should().BeTrue();

        await AssertSetAsideAsync(seeded);
    }

    [Fact]
    public async Task Recycling_a_lead_stops_it_too()
    {
        var seeded = await SeedAsync(lost: true);

        await using (var db = _factory.CreateContext())
            (await new RecycleLeadHandler(db, TimeProvider.System).Handle(
                new RecycleLeadCommand(seeded.Lead, ReactivateOn: DateOnly.FromDateTime(DateTime.UtcNow).AddDays(30)), default))
                .IsSuccess.Should().BeTrue();

        await AssertSetAsideAsync(seeded);
    }

    [Fact]
    public async Task A_bulk_recycle_stops_it_too()
    {
        var seeded = await SeedAsync(lost: true);

        await using (var db = _factory.CreateContext())
            (await new BulkRecycleHandler(db, TimeProvider.System).Handle(
                new BulkRecycleCommand([seeded.Lead]), default)).Value!.Succeeded.Should().Be(1);

        await AssertSetAsideAsync(seeded);
    }

    [Fact]
    public async Task A_refused_recycle_changes_nothing()
    {
        var seeded = await SeedAsync();

        await using (var db = _factory.CreateContext())
            (await new RecycleLeadHandler(db, TimeProvider.System).Handle(new RecycleLeadCommand(seeded.Lead), default))
                .IsFailure.Should().BeTrue();

        await using var read = _factory.CreateContext();
        (await read.CrmTasks.SingleAsync(t => t.Id == seeded.FirstContact)).Status.Should().Be(CrmTaskStatus.Pending);
        (await read.LeadAssignments.SingleAsync(a => a.Id == seeded.Assignment)).IsSuperseded.Should().BeFalse();
    }

    [Fact]
    public async Task A_contact_already_recorded_stays_counted()
    {
        var seeded = await SeedAsync(contacted: true);

        await using (var db = _factory.CreateContext())
            (await new NurtureLeadHandler(db, TimeProvider.System).Handle(new NurtureLeadCommand(seeded.Lead, seeded.Sequence), default))
                .IsSuccess.Should().BeTrue();

        await using var read = _factory.CreateContext();
        var assignment = await read.LeadAssignments.SingleAsync(a => a.Id == seeded.Assignment);
        assignment.IsSuperseded.Should().BeFalse();
        assignment.FirstContactAt.Should().NotBeNull();
    }
}
