namespace Sankore.Modules.Leads.Tests.LeadAggregate;

using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Sankore.Modules.Integration.PublicApi;
using Sankore.Modules.Leads.Domain;
using Sankore.Modules.Leads.Features.Consumers;
using Sankore.Modules.Leads.Features.RecordFirstContact;
using Sankore.Modules.Leads.Tests.TestSupport;
using Xunit;

/// <summary>
/// When a lead becomes "Contacté". Nothing did it before: a first contact stopped the SLA and closed
/// the task, and the lead stayed New until somebody dragged its card.
/// </summary>
public sealed class LeadContactProgressionTests : IDisposable
{
    private readonly Guid _tenantId = Guid.NewGuid();
    private readonly Guid _agent = Guid.NewGuid();
    private readonly TestDbContextFactory _db;

    public LeadContactProgressionTests() => _db = new TestDbContextFactory(_tenantId);

    public void Dispose() => _db.Dispose();

    // ── The aggregate ───────────────────────────────────────────────────────

    [Fact]
    public void An_established_contact_opens_a_new_lead_and_moves_it_to_contact_established()
    {
        var lead = Fresh();

        lead.RecordContactEstablished().Should().BeTrue();

        lead.Status.Should().Be(LeadStatus.Open);
        lead.PipelineStage.Should().Be(PipelineStage.ContactEstablished);
    }

    [Fact]
    public void An_attempt_only_moves_the_stage_and_never_after_a_contact()
    {
        var lead = Fresh();

        lead.RecordContactAttempt().Should().BeTrue();
        lead.Status.Should().Be(LeadStatus.New, "nobody has been reached yet");
        lead.PipelineStage.Should().Be(PipelineStage.ContactAttempted);

        lead.RecordContactEstablished();
        lead.RecordContactAttempt().Should().BeFalse("an attempt never undoes a contact");
        lead.PipelineStage.Should().Be(PipelineStage.ContactEstablished);
    }

    [Fact]
    public void A_lead_already_further_along_is_never_moved_back()
    {
        var lead = LeadTestBuilder.Create().WithTenant(_tenantId).Build();   // Qualified
        lead.AdvancePipelineStage(PipelineStage.DocumentCollection);

        lead.RecordContactEstablished().Should().BeFalse();

        lead.Status.Should().Be(LeadStatus.Qualified);
        lead.PipelineStage.Should().Be(PipelineStage.DocumentCollection);
    }

    [Fact]
    public void A_closed_lead_is_left_alone()
    {
        var lead = Fresh();
        lead.Close(LeadCloseReason.Lost, "Pas intéressé");

        lead.RecordContactEstablished().Should().BeFalse();
        lead.Status.Should().Be(LeadStatus.Lost);
    }

    [Theory]
    [InlineData(ActivityType.Call, ActivityOutcome.Reached, true)]
    [InlineData(ActivityType.Meeting, ActivityOutcome.Completed, true)]
    [InlineData(ActivityType.Call, ActivityOutcome.NoAnswer, false)]
    [InlineData(ActivityType.Call, ActivityOutcome.Voicemail, false)]
    [InlineData(ActivityType.Call, ActivityOutcome.Callback, false)]
    [InlineData(ActivityType.Email, ActivityOutcome.Reached, null)]
    [InlineData(ActivityType.Note, ActivityOutcome.Completed, null)]
    public void A_logged_activity_says_reached_not_reached_or_nothing(ActivityType type, ActivityOutcome outcome, bool? expected)
        => Lead.IsContactOutcome(type, outcome).Should().Be(expected);

    [Theory]
    [InlineData(LeadCloseReason.Lost)]
    [InlineData(LeadCloseReason.Disqualified)]
    public void A_closed_lead_can_be_reopened_and_forgets_why_it_was_lost(LeadCloseReason reason)
    {
        var lead = Fresh();
        lead.Close(reason, "Budget insuffisant");

        lead.Reopen().IsSuccess.Should().BeTrue();

        lead.Status.Should().Be(LeadStatus.Open);
        lead.LossReason.Should().BeNull();
    }

    [Fact]
    public void A_converted_lead_cannot_be_reopened()
    {
        var lead = Fresh();
        lead.Convert(Guid.NewGuid());

        lead.Reopen().IsSuccess.Should().BeFalse();
        lead.Status.Should().Be(LeadStatus.Converted);
    }

    // ── The paths that record a contact, persisted ──────────────────────────

    [Fact]
    public async Task The_agents_declared_first_contact_is_saved_on_the_lead()
    {
        var lead = await SeedAssignedAsync();

        await using (var db = _db.CreateContext())
            (await new RecordFirstContactHandler(db).Handle(new RecordFirstContactCommand(lead.Id), default))
                .IsSuccess.Should().BeTrue();

        var saved = await ReloadAsync(lead.Id);
        saved.Status.Should().Be(LeadStatus.Open);
        saved.PipelineStage.Should().Be(PipelineStage.ContactEstablished);
    }

    [Fact]
    public async Task An_outbound_call_without_contact_is_an_attempt_and_one_that_counts_is_a_contact()
    {
        var lead = await SeedAssignedAsync();

        await WrapUpAsync(lead.Id, "NO_ANSWER", countsAsContact: false, direction: "Outbound", callRef: "c1");
        var afterAttempt = await ReloadAsync(lead.Id);
        afterAttempt.Status.Should().Be(LeadStatus.New);
        afterAttempt.PipelineStage.Should().Be(PipelineStage.ContactAttempted);

        await WrapUpAsync(lead.Id, "REACHED", countsAsContact: true, direction: "Outbound", callRef: "c2");
        var afterContact = await ReloadAsync(lead.Id);
        afterContact.Status.Should().Be(LeadStatus.Open);
        afterContact.PipelineStage.Should().Be(PipelineStage.ContactEstablished);
    }

    // ── Harness ─────────────────────────────────────────────────────────────

    private Lead Fresh() => LeadTestBuilder.Create().WithTenant(_tenantId).Fresh().Build();

    private async Task<Lead> SeedAssignedAsync()
    {
        var lead = Fresh();
        var assignment = LeadAssignment.Create(
            _tenantId, lead.Id, _agent, DispatchingStrategy.RoundRobin, 50,
            slaDeadline: DateTimeOffset.UtcNow.AddHours(4), createdAt: DateTimeOffset.UtcNow);
        lead.AssignTo(assignment, currentAssignment: null).IsSuccess.Should().BeTrue();
        lead.ClearDomainEvents();

        await using var db = _db.CreateContext();
        db.Leads.Add(lead);
        db.LeadAssignments.Add(assignment);
        await db.SaveChangesAsync();
        return lead;
    }

    private async Task<Lead> ReloadAsync(Guid id)
    {
        await using var db = _db.CreateContext();
        return await db.Leads.FirstAsync(l => l.Id == id);
    }

    private async Task WrapUpAsync(Guid leadId, string outcome, bool countsAsContact, string direction, string callRef)
    {
        await using var db = _db.CreateContext();
        await TelephonyCallWrappedUpConsumer.ApplyAsync(db, NullLogger.Instance, new TelephonyCallWrappedUpEvent(
            TenantId: _tenantId, CallId: Guid.NewGuid(), ConnectionId: Guid.NewGuid(), ExternalCallId: callRef,
            EntityType: TelephonyEntityTypes.Lead, EntityId: leadId, CrmUserId: _agent,
            Direction: direction, Status: "Completed", OutcomeCode: outcome, OutcomeLabel: outcome,
            CountsAsContact: countsAsContact, StartedAt: DateTimeOffset.UtcNow.AddMinutes(-5), TalkSeconds: 30,
            CallbackAt: null, WrappedUpAt: DateTimeOffset.UtcNow), default);
    }
}
