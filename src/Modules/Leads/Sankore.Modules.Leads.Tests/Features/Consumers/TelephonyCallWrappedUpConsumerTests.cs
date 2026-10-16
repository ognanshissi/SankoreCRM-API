namespace Sankore.Modules.Leads.Tests.Features.Consumers;

using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Sankore.Modules.Integration.PublicApi;
using Sankore.Modules.Leads.Domain;
using Sankore.Modules.Leads.Features.Consumers;
using Sankore.Modules.Leads.Tests.TestSupport;
using Sankore.Shared.Kernel.ValueObject;
using Xunit;

/// <summary>
/// What a qualified call becomes in a lead's own history (TEL-B-11) and what it does to the
/// first-contact clock (TEL-B-12).
/// </summary>
public sealed class TelephonyCallWrappedUpConsumerTests : IDisposable
{
    private readonly Guid _tenant = Guid.NewGuid();
    private readonly Guid _agent = Guid.NewGuid();
    private readonly TestDbContextFactory _factory;

    /// <summary>When the call ran — twenty minutes before the agent finished typing.</summary>
    private readonly DateTimeOffset _calledAt =
        new(2026, 3, 11, 9, 0, 0, TimeSpan.Zero);

    public TelephonyCallWrappedUpConsumerTests() => _factory = new TestDbContextFactory(_tenant);

    public void Dispose() => _factory.Dispose();

    [Fact]
    public async Task A_qualified_call_becomes_the_leads_Call_activity()
    {
        var lead = await SeedLeadAsync();

        await ApplyAsync(Event(lead.Id, "REACHED", countsAsContact: true, talkSeconds: 95));

        await using var db = _factory.CreateContext();
        var activity = db.LeadActivities.Single();

        activity.Type.Should().Be(ActivityType.Call);
        activity.LeadId.Should().Be(lead.Id);
        activity.CtiCallReference.Should().Be("prov-1");
        activity.IsSystemGenerated.Should().BeTrue();
        activity.Outcome.Should().Be(ActivityOutcome.Reached);

        // The AGENT, not SYSTEM: they had the conversation. The system-generated flag says the row
        // was written by machinery, which is a different fact — and keeping them separate is what
        // lets an agent performance figure count this call.
        activity.PerformedBy.Should().Be(_agent);

        // 95 seconds rounds UP to 2 minutes: a floor would report a day of forty-second calls as
        // zero minutes of work.
        activity.DurationMinutes.Should().Be(2);
    }

    [Fact]
    public async Task The_activity_is_dated_at_the_CALL_and_not_at_the_wrap_up()
    {
        var lead = await SeedLeadAsync();

        await ApplyAsync(Event(lead.Id, "REACHED", countsAsContact: true));

        await using var db = _factory.CreateContext();

        // Until TEL-B-11 LeadActivity.Create forced UtcNow, so an activity was dated by the length
        // of the agent's paperwork — and every "when did we last speak to this person" with it.
        db.LeadActivities.Single().PerformedAt.Should().Be(_calledAt);
    }

    [Fact]
    public async Task The_subject_names_the_direction_and_the_outcome_and_never_a_number()
    {
        var lead = await SeedLeadAsync();

        await ApplyAsync(Event(lead.Id, "REACHED", countsAsContact: true, direction: "Inbound"));

        await using var db = _factory.CreateContext();
        var subject = db.LeadActivities.Single().Subject;

        // Direction first, because it is the question a reader of a history asks and the one the
        // outcome label never answers.
        subject.Should().Be("Appel entrant — Joint");

        // This subject is projected into the client timeline at conversion, where
        // TimelineSummaryGuard redacts anything that looks like a number AND logs a warning about
        // it — so a subject built from one would be both redacted and an alert, every single call.
        subject.Should().NotMatchRegex(@"\d{7,}");
    }

    [Fact]
    public async Task No_note_is_copied_into_the_lead()
    {
        var lead = await SeedLeadAsync();

        await ApplyAsync(Event(lead.Id, "REACHED", countsAsContact: true));

        await using var db = _factory.CreateContext();

        // The event does not carry one and this consumer writes none: an agent's note is free text
        // about a person, and it would cross into the client timeline — a store with no reveal
        // audit.
        db.LeadActivities.Single().Notes.Should().BeNull();
    }

    // ── TEL-B-12 ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task An_outcome_that_counts_as_a_contact_stops_the_SLA_clock()
    {
        var lead = await SeedLeadAsync();

        await ApplyAsync(Event(lead.Id, "REACHED", countsAsContact: true));

        await using var db = _factory.CreateContext();
        var assignment = db.LeadAssignments.Single();

        // Dated at the CALL: the SLA question is "how long before somebody spoke to this lead",
        // and answering it with the moment the agent finished typing flatters every figure.
        assignment.FirstContactAt.Should().Be(_calledAt);

        // A claim and evidence are different things, and the column is what lets a manager tell
        // a team that answers its leads from one that has learned to press the button.
        assignment.FirstContactSource.Should().Be(FirstContactSource.TelephonyCall);
    }

    [Fact]
    public async Task An_outcome_that_does_not_count_leaves_the_clock_running()
    {
        var lead = await SeedLeadAsync();

        // Leaving a message is not reaching somebody, and the institution decides which of its
        // outcomes mean what.
        await ApplyAsync(Event(lead.Id, "VOICEMAIL", countsAsContact: false));

        await using var db = _factory.CreateContext();

        db.LeadActivities.Should().HaveCount(1, "the call still happened");
        db.LeadAssignments.Single().FirstContactAt.Should().BeNull();
    }

    [Fact]
    public async Task A_first_contact_already_recorded_is_never_rewritten()
    {
        var lead = await SeedLeadAsync();

        await using (var db = _factory.CreateContext())
        {
            var assignment = await db.LeadAssignments.AsTracking().SingleAsync(CancellationToken.None);
            assignment.RecordFirstContact(
                _calledAt.AddDays(-1), FirstContactSource.AgentDeclared);
            await db.SaveChangesAsync(CancellationToken.None);
        }

        await ApplyAsync(Event(lead.Id, "REACHED", countsAsContact: true));

        await using var after = _factory.CreateContext();
        var row = after.LeadAssignments.Single();

        // The instant AND its source stay as they were: a source written on its own would say a
        // telephony call established a contact an agent had already declared.
        row.FirstContactAt.Should().Be(_calledAt.AddDays(-1));
        row.FirstContactSource.Should().Be(FirstContactSource.AgentDeclared);
    }

    // ── Idempotency and the callback ──────────────────────────────────────────

    [Fact]
    public async Task A_redelivered_event_writes_one_activity()
    {
        var lead = await SeedLeadAsync();

        var evt = Event(lead.Id, "REACHED", countsAsContact: true);

        await ApplyAsync(evt);
        await ApplyAsync(evt);

        await using var db = _factory.CreateContext();

        // Delivery is at-least-once. A second activity would show one conversation as two, in the
        // lead's history and in the activity counts every performance figure is built from.
        db.LeadActivities.Should().HaveCount(1);
    }

    [Fact]
    public async Task A_corrected_outcome_does_not_rewrite_the_activity()
    {
        var lead = await SeedLeadAsync();

        await ApplyAsync(Event(lead.Id, "REACHED", countsAsContact: true));
        await ApplyAsync(Event(lead.Id, "NOT_INTERESTED", countsAsContact: true));

        await using var db = _factory.CreateContext();
        var activity = db.LeadActivities.Should().ContainSingle().Subject;

        // An activity is immutable by documentation, and re-qualifying a call is a second opinion
        // about a conversation rather than a different conversation. The telephony journal holds
        // the current outcome; the history holds what was first recorded.
        activity.Outcome.Should().Be(ActivityOutcome.Reached);
    }

    [Fact]
    public async Task A_callback_date_produces_a_pending_reminder()
    {
        var lead = await SeedLeadAsync();

        var due = _calledAt.AddDays(2);

        await ApplyAsync(Event(lead.Id, "CALLBACK", countsAsContact: false, callbackAt: due));

        await using var db = _factory.CreateContext();
        var reminder = db.LeadReminders.Should().ContainSingle().Subject;

        reminder.DueAt.Should().Be(due);
        reminder.Status.Should().Be(ReminderStatus.Pending);

        // The outcome's own label, so an institution that renamed "À rappeler" sees its own words.
        reminder.Title.Should().Be("Rappel — À rappeler");
    }

    [Fact]
    public async Task A_call_attached_to_a_CLIENT_is_not_this_consumers_business()
    {
        var lead = await SeedLeadAsync();

        await ApplyAsync(Event(lead.Id, "REACHED", countsAsContact: true) with
        {
            EntityType = TelephonyEntityTypes.Customer,
        });

        await using var db = _factory.CreateContext();

        // M01 reads the telephony journal through the gateway; writing a lead activity for a
        // client's call would file it against whatever lead the id happened to match.
        db.LeadActivities.Should().BeEmpty();
    }

    [Fact]
    public async Task A_lead_that_no_longer_exists_is_skipped_without_throwing()
    {
        // An at-least-once consumer must not blow up on a lead purged or anonymised between the
        // call and the message.
        var act = async () => await ApplyAsync(
            Event(Guid.NewGuid(), "REACHED", countsAsContact: true));

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task An_unrecognised_outcome_code_degrades_to_the_contact_flag()
    {
        var lead = await SeedLeadAsync();

        // An institution WILL add an outcome of its own. ActivityOutcome is a platform enum shared
        // by calls, meetings and visits, so the only part of a tenant's code the platform may
        // assume is the flag the event already carries.
        await ApplyAsync(Event(lead.Id, "RELANCE_COMMERCIALE", countsAsContact: true));

        await using var db = _factory.CreateContext();
        db.LeadActivities.Single().Outcome.Should().Be(ActivityOutcome.Reached);
    }

    // ── Harness ───────────────────────────────────────────────────────────────

    private TelephonyCallWrappedUpEvent Event(
        Guid leadId,
        string outcomeCode,
        bool countsAsContact,
        int? talkSeconds = 60,
        DateTimeOffset? callbackAt = null,
        string direction = "Inbound")
        => new(
            TenantId: _tenant,
            CallId: Guid.NewGuid(),
            ConnectionId: Guid.NewGuid(),
            ExternalCallId: "prov-1",
            EntityType: TelephonyEntityTypes.Lead,
            EntityId: leadId,
            CrmUserId: _agent,
            Direction: direction,
            Status: "Completed",
            OutcomeCode: outcomeCode,
            OutcomeLabel: Label(outcomeCode),
            CountsAsContact: countsAsContact,
            StartedAt: _calledAt,
            TalkSeconds: talkSeconds,
            CallbackAt: callbackAt,
            WrappedUpAt: _calledAt.AddMinutes(20));

    private static string Label(string code) => code switch
    {
        "REACHED" => "Joint",
        "NOT_INTERESTED" => "Joint — pas intéressé",
        "CALLBACK" => "À rappeler",
        "VOICEMAIL" => "Messagerie",
        _ => code,
    };

    private async Task<Lead> SeedLeadAsync()
    {
        await using var db = _factory.CreateContext();

        var lead = Lead.Capture(
            tenantId: _tenant,
            fullName: "Aya Kouassi",
            phoneNumber: "+2250700000001",
            source: LeadSource.CallCenter,
            interestedProduct: "Crédit commerçant",
            preferredLanguage: "fr",
            location: new GeoPoint(5.3, -4.0),
            preferredAgencyId: null,
            clock: TimeProvider.System);

        var assignment = LeadAssignment.Create(
            tenantId: _tenant,
            leadId: lead.Id,
            agentId: _agent,
            strategy: DispatchingStrategy.RoundRobin,
            compatibilityScore: 80,
            createdAt: _calledAt.AddHours(-2),
            slaDeadline: _calledAt.AddHours(2));

        db.Leads.Add(lead);
        db.LeadAssignments.Add(assignment);
        await db.SaveChangesAsync(CancellationToken.None);

        // Through the aggregate, so CurrentAssignmentId is set the way dispatching sets it.
        await using var tracking = _factory.CreateContext();
        var tracked = await tracking.Leads.AsTracking().SingleAsync(CancellationToken.None);
        tracked.AssignTo(assignment, currentAssignment: null).IsSuccess.Should().BeTrue();
        await tracking.SaveChangesAsync(CancellationToken.None);

        return lead;
    }

    private async Task ApplyAsync(TelephonyCallWrappedUpEvent evt)
    {
        await using var db = _factory.CreateContext();

        await TelephonyCallWrappedUpConsumer.ApplyAsync(
            db,
            NullLogger<TelephonyCallWrappedUpConsumer>.Instance,
            evt,
            CancellationToken.None);
    }
}
