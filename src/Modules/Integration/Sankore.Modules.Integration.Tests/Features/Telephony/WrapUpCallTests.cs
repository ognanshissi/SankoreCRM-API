namespace Sankore.Modules.Integration.Tests.Features.Telephony;

using System.Text;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Sankore.Modules.Integration.Domain;
using Sankore.Modules.Integration.Features.Telephony.Outcomes;
using Sankore.Modules.Integration.Features.Telephony.Recordings;
using Sankore.Modules.Integration.Features.Telephony.WrapUp;
using Sankore.Modules.Integration.PublicApi;
using Sankore.Modules.Integration.Tests.TestSupport;
using Sankore.Shared.Infrastructure.Messaging;
using Sankore.Shared.Kernel;
using Xunit;

/// <summary>
/// Qualifying a call (TEL-B-11): what the referential decides, and what crosses to M13.
/// </summary>
public sealed class WrapUpCallTests : IDisposable
{
    private readonly Guid _tenant = Guid.NewGuid();
    private readonly Guid _connection = Guid.NewGuid();
    private readonly Guid _agent = Guid.NewGuid();
    private readonly Guid _leadId = Guid.NewGuid();
    private readonly TestIntegrationDbContextFactory _factory;

    public WrapUpCallTests()
    {
        _factory = new TestIntegrationDbContextFactory(_tenant);

        using var db = _factory.CreateContext();
        db.Connections.Add(TelephonyTestHarness.ActiveTelephonyConnection(_tenant, _connection));
        db.SaveChanges();

        // The baseline every tenant gets at start-up. Seeded here rather than hand-built so the
        // cases run against the codes production actually has.
        TelCallOutcomeSeeder.SeedTenantAsync(db, _tenant, CancellationToken.None)
            .GetAwaiter().GetResult();
        db.SaveChanges();
    }

    public void Dispose() => _factory.Dispose();

    // ── The seeded referential ────────────────────────────────────────────────

    [Fact]
    public void The_baseline_marks_only_a_real_conversation_as_a_contact()
    {
        var counts = TelCallOutcomeDefaults.All
            .Where(o => o.CountsAsContact)
            .Select(o => o.Code)
            .ToList();

        // Leaving a message is NOT reaching somebody: counting VOICEMAIL would let an agent stop
        // a lead's SLA clock without ever having a conversation.
        counts.Should().BeEquivalentTo(["REACHED", "INTERESTED", "NOT_INTERESTED"]);
    }

    [Fact]
    public void Only_the_callback_outcome_demands_a_date()
    {
        // Making NO_ANSWER demand one would force a date onto every unanswered ring, which is how
        // a mandatory field becomes a field everybody fills with today.
        TelCallOutcomeDefaults.All
            .Where(o => o.RequiresCallbackDate)
            .Select(o => o.Code)
            .Should().BeEquivalentTo(["CALLBACK"]);
    }

    [Fact]
    public async Task Seeding_twice_adds_nothing_and_keeps_a_renamed_label()
    {
        await using (var db = _factory.CreateContext())
        {
            var outcome = await db.TelCallOutcomes.AsTracking()
                .FirstAsync(o => o.Code == "REACHED", CancellationToken.None);

            outcome.Rename("Contacté");
            outcome.Deactivate();

            await db.SaveChangesAsync(CancellationToken.None);
        }

        await using var after = _factory.CreateContext();

        var added = await TelCallOutcomeSeeder.SeedTenantAsync(after, _tenant, CancellationToken.None);

        added.Should().Be(0);

        var reached = await after.TelCallOutcomes
            .FirstAsync(o => o.Code == "REACHED", CancellationToken.None);

        // A baseline, not a reset: an administrator who renamed or switched one off keeps their
        // change across every restart.
        reached.Label.Should().Be("Contacté");
        reached.IsActive.Should().BeFalse();
    }

    // ── The wrap-up ───────────────────────────────────────────────────────────

    [Fact]
    public async Task A_completed_call_is_qualified_and_the_event_carries_no_note()
    {
        var call = await SeedCallAsync(TelCallStatus.Completed);

        var (result, events) = await WrapUpAsync(call.Id, "REACHED", note: "Rappeler le gérant.");

        result.IsSuccess.Should().BeTrue();

        await using var db = _factory.CreateContext();
        var row = db.TelCalls.Single();

        row.OutcomeCode.Should().Be("REACHED");
        row.WrappedUpAt.Should().NotBeNull();

        // Encrypted in the column, and the agent's words are nowhere in the clear.
        Encoding.UTF8.GetString(row.NoteEnc!).Should().StartWith("v1:").And.NotContain("gérant");

        var evt = events.OfType<TelephonyCallWrappedUpEvent>().Should().ContainSingle().Subject;

        evt.OutcomeCode.Should().Be("REACHED");
        evt.CountsAsContact.Should().BeTrue();
        evt.ExternalCallId.Should().Be(row.ExternalCallId);

        // The note is NOT on the event. M13's timeline projection excludes free-text bodies
        // because they cross into a store with no reveal audit, and the event must not be the way
        // round that.
        evt.GetType().GetProperties().Select(p => p.Name)
            .Should().NotContain("Note").And.NotContain("NoteEnc");
    }

    [Fact]
    public async Task The_event_is_dated_at_the_CALL_and_not_at_the_paperwork()
    {
        var call = await SeedCallAsync(TelCallStatus.Completed);

        var (_, events) = await WrapUpAsync(call.Id, "REACHED");

        var evt = events.OfType<TelephonyCallWrappedUpEvent>().Single();

        // Every SLA measured from this inherits the drift otherwise: an agent wrapping up at
        // 14:05 a call that ran at 13:45 must produce an activity dated 13:45.
        evt.StartedAt.Should().Be(call.StartedAt);
        evt.WrappedUpAt.Should().Be(TelephonyTestHarness.Now);
    }

    [Fact]
    public async Task A_call_that_has_not_ended_is_refused()
    {
        var call = await SeedCallAsync(TelCallStatus.InProgress);

        // The agent is still speaking. The aggregate owns the rule.
        var (result, _) = await WrapUpAsync(call.Id, "REACHED");

        result.Error.Should().Be(TelephonyErrors.CallNotEnded);
    }

    [Fact]
    public async Task An_outcome_the_tenant_does_not_have_is_refused()
    {
        var call = await SeedCallAsync(TelCallStatus.Completed);

        // Accepting an arbitrary string would qualify the call in a way nothing downstream
        // recognises: the behaviour flags come from the referential, not from the caller.
        var (result, events) = await WrapUpAsync(call.Id, "INVENTED_BY_A_SCREEN");

        result.Error.Should().Be(TelephonyErrors.OutcomeUnknown);
        events.Should().BeEmpty();
    }

    [Fact]
    public async Task A_DEACTIVATED_outcome_is_refused_like_an_unknown_one()
    {
        await using (var db = _factory.CreateContext())
        {
            var outcome = await db.TelCallOutcomes.AsTracking()
                .FirstAsync(o => o.Code == "VOICEMAIL", CancellationToken.None);

            outcome.Deactivate();
            await db.SaveChangesAsync(CancellationToken.None);
        }

        var call = await SeedCallAsync(TelCallStatus.Voicemail);

        // Deactivating is how an institution retires an outcome. A screen holding a stale list
        // must not be able to keep using it.
        var (result, _) = await WrapUpAsync(call.Id, "VOICEMAIL");

        result.Error.Should().Be(TelephonyErrors.OutcomeUnknown);
    }

    [Fact]
    public async Task An_outcome_that_demands_a_callback_date_refuses_without_one()
    {
        var call = await SeedCallAsync(TelCallStatus.Completed);

        var (refused, _) = await WrapUpAsync(call.Id, "CALLBACK");

        refused.Error.Should().Be(TelephonyErrors.CallbackDateRequired);

        var (accepted, events) = await WrapUpAsync(
            call.Id, "CALLBACK", callbackAt: TelephonyTestHarness.Now.AddDays(1));

        accepted.IsSuccess.Should().BeTrue();
        events.OfType<TelephonyCallWrappedUpEvent>().Single().CallbackAt.Should().NotBeNull();
    }

    [Fact]
    public async Task A_callback_date_on_an_outcome_that_does_not_ask_for_one_is_kept()
    {
        var call = await SeedCallAsync(TelCallStatus.Completed);

        // An agent noting "call back Thursday" on a "Joint" outcome is doing something sensible,
        // and refusing it would teach them to pick the wrong outcome to get the field.
        var (result, events) = await WrapUpAsync(
            call.Id, "REACHED", callbackAt: TelephonyTestHarness.Now.AddDays(2));

        result.IsSuccess.Should().BeTrue();
        events.OfType<TelephonyCallWrappedUpEvent>().Single().CallbackAt.Should().NotBeNull();
    }

    [Fact]
    public async Task Another_agents_call_cannot_be_wrapped_up()
    {
        var call = await SeedCallAsync(TelCallStatus.Completed, crmUserId: Guid.NewGuid());

        // Not found and not forbidden: a 403 would confirm a colleague spoke to somebody.
        var (result, events) = await WrapUpAsync(call.Id, "REACHED");

        result.Error.Should().Be(TelephonyErrors.CallNotFound);
        events.Should().BeEmpty();
    }

    [Fact]
    public async Task A_call_attributed_to_nobody_may_be_wrapped_up()
    {
        var call = await SeedCallAsync(TelCallStatus.Missed, attributed: false);

        // The agency's "to attribute" queue.
        var (result, _) = await WrapUpAsync(call.Id, TelCallOutcomeDefaults.NotReached);

        result.IsSuccess.Should().BeTrue();
    }

    [Fact]
    public async Task Wrapping_up_twice_is_allowed_and_the_last_outcome_stands()
    {
        var call = await SeedCallAsync(TelCallStatus.Completed);

        (await WrapUpAsync(call.Id, "REACHED")).Result.IsSuccess.Should().BeTrue();

        // An agent correcting themselves. The journal is the authority on the current outcome;
        // M13 is idempotent on the call, so the lead's history keeps what was first recorded.
        var (second, events) = await WrapUpAsync(call.Id, "NOT_INTERESTED");

        second.IsSuccess.Should().BeTrue();
        events.OfType<TelephonyCallWrappedUpEvent>().Should().ContainSingle();

        await using var db = _factory.CreateContext();
        db.TelCalls.Single().OutcomeCode.Should().Be("NOT_INTERESTED");
    }

    // ── Keeping the recording (TEL-B-15, OnDemand) ───────────────────────────

    [Fact]
    public async Task Asking_to_keep_the_recording_marks_the_call_under_ON_DEMAND()
    {
        await SetPolicyAsync(TelRecordingMode.OnDemand);

        var callId = (await SeedCallAsync(TelCallStatus.Completed)).Id;

        (await WrapUpAsync(callId, "REACHED", keepRecording: true)).Result
            .IsSuccess.Should().BeTrue();

        // The ONLY route by which a recording is ever kept under OnDemand. Before this, the mode
        // was selectable and unreachable: TelRecordingPolicy.Keeps reads KeepRecording and
        // MarkForRecording had no caller, so a compliance officer could choose "à la demande",
        // believe recording was on, and get nothing.
        (await ReloadAsync(callId)).KeepRecording.Should().BeTrue();
    }

    [Theory]
    [InlineData(TelRecordingMode.Disabled)]
    [InlineData(TelRecordingMode.AllCalls)]
    public async Task The_request_is_IGNORED_under_any_other_mode(TelRecordingMode mode)
    {
        await SetPolicyAsync(mode);

        var callId = (await SeedCallAsync(TelCallStatus.Completed)).Id;

        var (result, _) = await WrapUpAsync(callId, "REACHED", keepRecording: true);

        // Ignored and not refused: the box is rendered from a policy the browser read earlier, so
        // a tab older than a policy change is ordinary and must not cost the agent their
        // qualification. Under AllCalls the flag is redundant; under Disabled, honouring it would
        // be a way around the policy.
        result.IsSuccess.Should().BeTrue();

        (await ReloadAsync(callId)).KeepRecording.Should().BeFalse();
    }

    [Fact]
    public async Task With_no_policy_at_all_the_request_is_ignored()
    {
        var callId = (await SeedCallAsync(TelCallStatus.Completed)).Id;

        // No policy row: the reader answers Disabled, which is what an institution that has never
        // stated a legal basis must get.
        (await WrapUpAsync(callId, "REACHED", keepRecording: true)).Result
            .IsSuccess.Should().BeTrue();

        (await ReloadAsync(callId)).KeepRecording.Should().BeFalse();
    }

    [Fact]
    public async Task A_second_wrap_up_cannot_UN_ask_for_the_recording()
    {
        await SetPolicyAsync(TelRecordingMode.OnDemand);

        var callId = (await SeedCallAsync(TelCallStatus.Completed)).Id;

        await WrapUpAsync(callId, "REACHED", keepRecording: true);
        await WrapUpAsync(callId, "NOT_INTERESTED", keepRecording: false);

        // By the second wrap-up the fetch job may already hold the audio, and "forget it" is a
        // retention decision for the purge or for an anonymisation — not for a correction of an
        // outcome.
        (await ReloadAsync(callId)).KeepRecording.Should().BeTrue();
    }

    private async Task SetPolicyAsync(TelRecordingMode mode)
    {
        await using var db = _factory.CreateContext();

        var policy = TelRecordingPolicy.Create(_tenant, _agent, TelephonyTestHarness.Now);

        policy.Apply(
            mode,
            legalBasis: mode == TelRecordingMode.Disabled ? null : "Obligation légale CIMA",
            purpose: mode == TelRecordingMode.Disabled ? null : "Contrôle qualité",
            retentionDays: 90,
            deleteAtProvider: false,
            maxDurationSeconds: 3600,
            updatedBy: _agent,
            now: TelephonyTestHarness.Now)
            .IsSuccess.Should().BeTrue();

        db.TelRecordingPolicies.Add(policy);
        await db.SaveChangesAsync(CancellationToken.None);
    }

    private async Task<TelCall> ReloadAsync(Guid callId)
    {
        await using var db = _factory.CreateContext();

        return await db.TelCalls.FirstAsync(c => c.Id == callId, CancellationToken.None);
    }

    // ── Harness ───────────────────────────────────────────────────────────────

    /// <param name="attributed">
    /// Whether the call belongs to the acting agent. A BOOLEAN and not a nullable Guid, because
    /// the nullable form was a trap this harness had fallen into: it read
    /// <c>crmUserId == default ? _agent : crmUserId</c>, and <c>default</c> for a
    /// <c>Guid?</c> IS null — so the one test that passed <c>crmUserId: null</c> to exercise the
    /// unattributed branch seeded the call attributed to the agent and passed without ever
    /// reaching it.
    /// </param>
    private async Task<TelCall> SeedCallAsync(
        TelCallStatus status, Guid? crmUserId = null, bool attributed = true)
    {
        await using var db = _factory.CreateContext();

        var protector = TelephonyTestHarness.Numbers();
        var from = protector.Protect("+22507111111");
        var to = protector.Protect("+22507000001");

        var call = TelCall.Open(
            _tenant, _connection, $"prov-{Guid.NewGuid()}",
            TelCallDirection.Inbound, TelCallStatus.Ringing,
            from.Encrypted, from.BlindIndex, to.Encrypted, to.BlindIndex,
            TelephonyTestHarness.Now.AddMinutes(-20),
            externalUserId: null,
            // attributed decides, and crmUserId only names WHICH agent. There is no sentinel to
            // get wrong.
            crmUserId: attributed ? crmUserId ?? _agent : null);

        // Through the transitions rather than opened on a terminal status: a call reaches
        // Completed by being answered, and the wrap-up's own rule reads EndedAt.
        if (status != TelCallStatus.Ringing)
        {
            if (status == TelCallStatus.Completed)
                call.TryMoveTo(TelCallStatus.InProgress, TelephonyTestHarness.Now.AddMinutes(-19));

            call.TryMoveTo(status, TelephonyTestHarness.Now.AddMinutes(-18));
        }

        call.Attach(TelMatchStatus.Matched, TelMatchMethod.Number, TelEntityTypes.Lead, _leadId);

        db.TelCalls.Add(call);
        await db.SaveChangesAsync(CancellationToken.None);

        return call;
    }

    private async Task<(Result Result, List<object> Events)> WrapUpAsync(
        Guid callId,
        string outcomeCode,
        string? note = null,
        DateTimeOffset? callbackAt = null,
        bool keepRecording = false)
    {
        await using var db = _factory.CreateContext();

        var publisher = new RecordingPublisher();

        var result = await new WrapUpCallHandler(
                db,
                new TelephonyRecordingPolicyReader(db),
                TelephonyTestHarness.Encryptor(),
                publisher,
                TelephonyTestHarness.User(_tenant, _agent),
                TelephonyTestHarness.Clock,
                NullLogger<WrapUpCallHandler>.Instance)
            .Handle(
                new WrapUpCallCommand(callId, outcomeCode, note, callbackAt, keepRecording),
                CancellationToken.None);

        return (result, publisher.Published);
    }

    /// <summary>
    /// Records what was published rather than writing to the outbox. What the handler promises is
    /// that a refusal publishes nothing and a success publishes one event with no note on it; the
    /// outbox's own behaviour is tested where the outbox lives.
    /// </summary>
    private sealed class RecordingPublisher : IEventPublisher
    {
        public List<object> Published { get; } = [];

        public Task PublishAsync<TEvent>(TEvent @event, CancellationToken ct)
            where TEvent : IIntegrationEvent
        {
            Published.Add(@event!);

            return Task.CompletedTask;
        }
    }
}
