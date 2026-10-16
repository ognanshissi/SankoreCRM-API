namespace Sankore.Modules.Integration.Tests.Features.Telephony;

using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Sankore.Modules.Integration.Domain;
using Sankore.Modules.Integration.Features.Telephony.Outcomes;
using Sankore.Modules.Integration.Features.Telephony.Stats;
using Sankore.Modules.Integration.Tests.TestSupport;
using Xunit;

/// <summary>
/// The indicators (TEL-B-19): what the recomputation counts, what it refuses to count, and the
/// two properties the story actually asks for — <b>recalculable</b> and <b>internal calls
/// excluded</b>.
/// </summary>
public sealed class TelephonyStatsTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 3, 11, 12, 0, 0, TimeSpan.Zero);

    private static readonly DateOnly Today = new(2026, 3, 11);

    private readonly Guid _tenant = Guid.NewGuid();
    private readonly Guid _connection = Guid.NewGuid();
    private readonly Guid _agent = Guid.NewGuid();
    private readonly Guid _agency = Guid.NewGuid();
    private readonly TestIntegrationDbContextFactory _factory;

    public TelephonyStatsTests()
    {
        _factory = new TestIntegrationDbContextFactory(_tenant);

        using var db = _factory.CreateContext();
        db.Connections.Add(TelephonyTestHarness.ActiveTelephonyConnection(_tenant, _connection));
        db.SaveChanges();
    }

    public void Dispose() => _factory.Dispose();

    // ── What is counted ───────────────────────────────────────────────────────

    [Fact]
    public async Task A_day_of_calls_becomes_one_bucket_per_agent_and_agency()
    {
        await SeedAsync(TelCallStatus.Completed, talk: 90, ring: 6);
        await SeedAsync(TelCallStatus.Completed, talk: 30, ring: 4);
        await SeedAsync(TelCallStatus.Missed, direction: TelCallDirection.Inbound);
        await SeedAsync(TelCallStatus.Failed, direction: TelCallDirection.Outbound);

        var row = (await RecomputeAndReadAsync()).Should().ContainSingle().Subject;

        row.CrmUserId.Should().Be(_agent);
        row.AgencyId.Should().Be(_agency);
        row.AnsweredCount.Should().Be(2);
        row.MissedCount.Should().Be(1);
        row.FailedCount.Should().Be(1);
        row.TalkSeconds.Should().Be(120);
        row.RingSeconds.Should().Be(10);
    }

    [Fact]
    public async Task Voicemail_counts_as_missed_and_a_ringing_call_counts_as_neither()
    {
        await SeedAsync(TelCallStatus.Voicemail);
        await SeedAsync(TelCallStatus.Ringing);

        var row = (await RecomputeAndReadAsync()).Should().ContainSingle().Subject;

        // Somebody left a message, which is the clearest possible request for a call back — the
        // follow-up list treats the two as one thing and so does this.
        row.MissedCount.Should().Be(1);

        // A call in flight is not yet an outcome of any kind. Counting it as missed would have
        // the figure flicker upward every time a telephone rings.
        row.AnsweredCount.Should().Be(0);
        row.FailedCount.Should().Be(0);

        // It is still a call, so the direction counters see it.
        (row.InboundCount + row.OutboundCount).Should().Be(2);
    }

    [Fact]
    public async Task A_call_in_progress_is_already_answered()
    {
        await SeedAsync(TelCallStatus.InProgress);

        // A screen refreshed at eleven must show the call an agent is on, or the figure disagrees
        // with the handset in their hand.
        (await RecomputeAndReadAsync()).Single().AnsweredCount.Should().Be(1);
    }

    [Fact]
    public async Task An_INTERNAL_call_is_excluded_entirely()
    {
        await SeedAsync(TelCallStatus.Completed, talk: 60, direction: TelCallDirection.Outbound);
        await SeedAsync(
            TelCallStatus.Completed, talk: 3600, direction: TelCallDirection.Internal);

        var row = (await RecomputeAndReadAsync()).Should().ContainSingle().Subject;

        // Not merely left out of a counter — out of every one of them, durations included. An
        // agent spending the morning consulting their branch manager must not outrank one who
        // spent it on clients.
        row.OutboundCount.Should().Be(1);
        row.InboundCount.Should().Be(0);
        row.AnsweredCount.Should().Be(1);
        row.TalkSeconds.Should().Be(60);
    }

    [Fact]
    public async Task A_call_of_another_day_is_in_another_bucket()
    {
        await SeedAsync(TelCallStatus.Completed, at: Now);
        await SeedAsync(TelCallStatus.Completed, at: Now.AddDays(-1));

        var today = await RecomputeAndReadAsync();

        today.Should().ContainSingle().Which.AnsweredCount.Should().Be(1);

        // And the other day is reachable, which is what makes the range recomputation meaningful.
        // Two DAYS, one total: Totals groups by agent and agency, Days keeps the per-day rows a
        // chart is drawn from.
        var both = await RecomputeAndQueryAsync(from: Today.AddDays(-1), to: Today);

        both.Days.Should().HaveCount(2);
        both.Totals.Should().ContainSingle().Which.AnsweredCount.Should().Be(2);
    }

    // ── Qualification ─────────────────────────────────────────────────────────

    [Fact]
    public async Task An_outcome_that_counts_as_contact_feeds_the_contact_figure()
    {
        await SeedOutcomesAsync();

        await SeedAsync(TelCallStatus.Completed, outcome: "REACHED");
        await SeedAsync(TelCallStatus.Completed, outcome: "NOT_INTERESTED");
        await SeedAsync(TelCallStatus.Completed, outcome: "WRONG_NUMBER");
        await SeedAsync(TelCallStatus.Completed);

        var row = (await RecomputeAndReadAsync()).Should().ContainSingle().Subject;

        // Three were qualified; a wrong number is not a conversation with anybody.
        row.WrappedUpCount.Should().Be(3);
        row.ContactCount.Should().Be(2);
    }

    [Fact]
    public async Task Changing_what_counts_as_contact_changes_the_HISTORY_on_the_next_recomputation()
    {
        await SeedOutcomesAsync();
        await SeedAsync(TelCallStatus.Completed, outcome: "WRONG_NUMBER");

        (await RecomputeAndReadAsync()).Single().ContactCount.Should().Be(0);

        // An institution that discovers an outcome was configured wrong wants last month's
        // figures corrected with it. A flag copied onto the call at wrap-up time would have
        // frozen the mistake into the history for ever.
        await using (var db = _factory.CreateContext())
        {
            var outcome = await db.TelCallOutcomes.AsTracking()
                .FirstAsync(o => o.Code == "WRONG_NUMBER", CancellationToken.None);

            db.TelCallOutcomes.Remove(outcome);

            db.TelCallOutcomes.Add(TelCallOutcome.Create(
                _tenant, "WRONG_NUMBER", "Mauvais numéro",
                countsAsContact: true, requiresCallbackDate: false, displayOrder: 70,
                isSystem: true));

            await db.SaveChangesAsync(CancellationToken.None);
        }

        (await RecomputeAndReadAsync()).Single().ContactCount.Should().Be(1);
    }

    // ── Recalculable ──────────────────────────────────────────────────────────

    [Fact]
    public async Task Recomputing_the_same_day_twice_gives_the_same_figures()
    {
        await SeedAsync(TelCallStatus.Completed, talk: 90);
        await SeedAsync(TelCallStatus.Missed);

        var first = await RecomputeAndReadAsync();
        var second = await RecomputeAndReadAsync();
        var third = await RecomputeAndReadAsync();

        // The property TEL-B-19 asks for, and the reason nothing here is an increment: a counter
        // that drifted would have no way back, because the outbox is not an event store.
        second.Should().BeEquivalentTo(first);
        third.Should().BeEquivalentTo(first);
    }

    [Fact]
    public async Task A_bucket_whose_calls_left_is_DELETED_rather_than_zeroed()
    {
        var call = await SeedAsync(TelCallStatus.Completed, talk: 60);

        (await RecomputeAndReadAsync()).Should().HaveCount(1);

        // Re-attributed to a colleague of another branch — a supervisor's correction.
        await using (var db = _factory.CreateContext())
        {
            var row = await db.TelCalls.AsTracking()
                .FirstAsync(c => c.Id == call, CancellationToken.None);

            db.TelCalls.Remove(row);
            await db.SaveChangesAsync(CancellationToken.None);
        }

        // A row of noughts left behind would keep that agent in every list built from this table
        // — which is how a "most active agents" screen ends up naming somebody who made no call.
        (await RecomputeAndReadAsync()).Should().BeEmpty();

        await using var check = _factory.CreateContext();

        (await check.TelCallDailyStats.CountAsync(CancellationToken.None))
            .Should().Be(0);
    }

    [Fact]
    public async Task A_period_over_the_ceiling_is_refused_and_not_truncated()
    {
        await using var db = _factory.CreateContext();

        var report = await TelCallStatsRecomputer.RecomputeRangeAsync(
            db, Clock(), NullLogger.Instance, _tenant,
            Today.AddDays(-TelCallStatsRecomputer.MaxDays), Today,
            CancellationToken.None);

        // A caller who asked for ten years and silently got the first four hundred days would
        // read the answer as complete.
        report.Refused.Should().BeTrue();
        report.Days.Should().Be(0);
    }

    [Fact]
    public async Task A_range_given_backwards_is_recomputed_anyway()
    {
        await SeedAsync(TelCallStatus.Completed, at: Now.AddDays(-2));

        await using var db = _factory.CreateContext();

        var report = await TelCallStatsRecomputer.RecomputeRangeAsync(
            db, Clock(), NullLogger.Instance, _tenant, Today, Today.AddDays(-3),
            CancellationToken.None);

        report.Refused.Should().BeFalse();
        report.Days.Should().Be(4);
        report.Buckets.Should().Be(1);
    }

    // ── The unattributed bucket ───────────────────────────────────────────────

    [Fact]
    public async Task A_call_nobody_owns_lands_in_its_own_bucket_and_leaves_as_NULL()
    {
        await SeedAsync(TelCallStatus.Missed, attributed: false);

        var row = (await RecomputeAndReadAsync()).Should().ContainSingle().Subject;

        // Guid.Empty is the table's sentinel — it makes the unique index constrain every row —
        // and it does not leave this module.
        row.CrmUserId.Should().BeNull();
        row.AgencyId.Should().BeNull();

        await using var db = _factory.CreateContext();

        var stored = await db.TelCallDailyStats
            .FirstAsync(CancellationToken.None);

        stored.CrmUserId.Should().Be(Guid.Empty);
        stored.AgencyId.Should().Be(Guid.Empty);
    }

    [Fact]
    public async Task A_scoped_caller_does_not_see_the_unattributed_bucket()
    {
        await SeedAsync(TelCallStatus.Completed);
        await SeedAsync(TelCallStatus.Missed, attributed: false);

        // Unrestricted: both.
        (await RecomputeAndReadAsync()).Should().HaveCount(2);

        // Scoped to the one agency: showing a call this module could not place inside a branch's
        // figures would attribute somebody else's work to that branch.
        var scoped = await ReadAsync(scope: [_agency]);

        scoped.Should().ContainSingle().Which.AgencyId.Should().Be(_agency);
    }

    [Fact]
    public async Task An_agency_outside_the_perimeter_answers_an_empty_period_and_not_a_refusal()
    {
        await SeedAsync(TelCallStatus.Completed);
        await RecomputeAndReadAsync();

        var other = Guid.NewGuid();

        var result = await QueryAsync(
            new GetTelephonyStatsQuery(Today, Today, AgencyId: other), scope: [_agency]);

        // A refusal would confirm the agency exists.
        result.IsSuccess.Should().BeTrue();
        result.Value.Totals.Should().BeEmpty();
        result.Value.Days.Should().BeEmpty();
        result.Value.ComputedAt.Should().BeNull();
    }

    [Fact]
    public async Task A_period_over_the_ceiling_is_refused_by_the_query_too()
    {
        var result = await QueryAsync(
            new GetTelephonyStatsQuery(Today.AddDays(-TelCallStatsRecomputer.MaxDays), Today),
            scope: null);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Be(TelephonyErrors.StatsPeriodTooLong);
    }

    [Fact]
    public async Task The_answer_says_how_old_the_figures_are()
    {
        await SeedAsync(TelCallStatus.Completed);

        var result = await RecomputeAndQueryAsync();

        // So a screen can say "as of 11:20" instead of implying the figures are live.
        result.ComputedAt.Should().Be(Now);
    }

    [Fact]
    public async Task Another_tenants_calls_are_invisible()
    {
        await SeedAsync(TelCallStatus.Completed);

        var other = Guid.NewGuid();

        await using (var db = _factory.CreateContext())
        {
            var protector = TelephonyTestHarness.Numbers();
            var number = protector.Protect("+22507999999");

            db.TelCalls.Add(TelCall.Open(
                other, _connection, "other-tenant-call",
                TelCallDirection.Inbound, TelCallStatus.Completed,
                number.Encrypted, number.BlindIndex, number.Encrypted, number.BlindIndex,
                Now, crmUserId: Guid.NewGuid(), agencyId: Guid.NewGuid()));

            await db.SaveChangesAsync(CancellationToken.None);
        }

        // The recomputation names the tenant in its predicate and the read leans on the query
        // filter: one bucket, not two.
        (await RecomputeAndReadAsync()).Should().ContainSingle();
    }

    // ── Harness ───────────────────────────────────────────────────────────────

    private static TimeProvider Clock() => new TelephonyTestHarness.FixedClock(Now);

    private async Task<Guid> SeedAsync(
        TelCallStatus status,
        int? talk = null,
        int? ring = null,
        string? outcome = null,
        DateTimeOffset? at = null,
        TelCallDirection direction = TelCallDirection.Inbound,
        bool attributed = true)
    {
        await using var db = _factory.CreateContext();

        var startedAt = at ?? Now;
        var protector = TelephonyTestHarness.Numbers();
        var number = protector.Protect("+22507111111");

        var call = TelCall.Open(
            _tenant, _connection, $"prov-{Guid.NewGuid()}",
            direction, TelCallStatus.Ringing,
            number.Encrypted, number.BlindIndex, number.Encrypted, number.BlindIndex,
            startedAt,
            externalUserId: null,
            crmUserId: attributed ? _agent : null,
            clickToCallId: null,
            agencyId: attributed ? _agency : null);

        if (status != TelCallStatus.Ringing)
            call.TryMoveTo(status, startedAt.AddSeconds(20));

        call.RecordDurations(ring, talk);

        if (outcome is not null)
            call.WrapUp(outcome, noteEnc: null, startedAt.AddMinutes(2)).IsSuccess.Should().BeTrue();

        db.TelCalls.Add(call);
        await db.SaveChangesAsync(CancellationToken.None);

        return call.Id;
    }

    private async Task SeedOutcomesAsync()
    {
        await using var db = _factory.CreateContext();

        // The real seeder, not a hand-built list: the figures depend on which outcomes count as
        // contact, so a fixture with its own opinion about that would be testing the fixture.
        // SeedTenantAsync adds and does NOT save — its production caller owns the transaction.
        await TelCallOutcomeSeeder.SeedTenantAsync(db, _tenant, CancellationToken.None);
        await db.SaveChangesAsync(CancellationToken.None);
    }

    private async Task<IReadOnlyList<TelephonyStatsRowDto>> RecomputeAndReadAsync(
        DateOnly? from = null, DateOnly? to = null)
        => (await RecomputeAndQueryAsync(from, to)).Totals;

    private async Task<TelephonyStatsDto> RecomputeAndQueryAsync(
        DateOnly? from = null, DateOnly? to = null)
    {
        await using (var db = _factory.CreateContext())
        {
            await TelCallStatsRecomputer.RecomputeRangeAsync(
                db, Clock(), NullLogger.Instance, _tenant,
                from ?? Today, to ?? Today, CancellationToken.None);
        }

        var result = await QueryAsync(
            new GetTelephonyStatsQuery(from ?? Today, to ?? Today), scope: null);

        result.IsSuccess.Should().BeTrue();

        return result.Value;
    }

    private async Task<IReadOnlyList<TelephonyStatsRowDto>> ReadAsync(Guid[]? scope)
        => (await QueryAsync(new GetTelephonyStatsQuery(Today, Today), scope)).Value.Totals;

    /// <param name="scope">
    /// Null is UNRESTRICTED, which is what <c>IAgencyScopeProvider</c> means by a null set — not
    /// "no access". Spelled at every call site for that reason.
    /// </param>
    private async Task<Sankore.Shared.Kernel.Result<TelephonyStatsDto>> QueryAsync(
        GetTelephonyStatsQuery query, Guid[]? scope)
    {
        await using var db = _factory.CreateContext();

        return await new GetTelephonyStatsHandler(
                db,
                TelephonyTestHarness.AgencyScope(scope),
                TelephonyTestHarness.User(_tenant, _agent))
            .Handle(query, CancellationToken.None);
    }
}
