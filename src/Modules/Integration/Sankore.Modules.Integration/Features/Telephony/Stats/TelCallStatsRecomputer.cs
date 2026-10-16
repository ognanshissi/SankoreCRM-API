namespace Sankore.Modules.Integration.Features.Telephony.Stats;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Sankore.Modules.Integration.Domain;
using Sankore.Modules.Integration.Infrastructure;

/// <summary>
/// Derives one tenant's indicators for one day, from the journal (TEL-B-19).
///
/// <para>
/// <b>One code path, three triggers.</b> The wrap-up consumer calls it for the day of the call it
/// just saw, the nightly job calls it for a window, and an administrator may call it for a range
/// they name. All three produce the same rows, because none of them adds to anything: the day is
/// counted from scratch and the bucket is overwritten. That is what makes the model recalculable,
/// which is the requirement TEL-B-19 actually states, and it is why there is no "stats are out of
/// step" incident to handle — the repair is to run it again.
/// </para>
///
/// <para>
/// <b>What it refuses to be.</b> Not an increment on an event, because a counter that drifts has
/// no way back: the outbox is not an event store, so "reset and replay" is not available, and the
/// first replayed webhook or re-qualified call would make every figure quietly wrong. Not a view
/// over <c>tel_call</c> either, because the per-agency screen scans months and the journal is the
/// hottest table of this schema.
/// </para>
/// </summary>
internal static class TelCallStatsRecomputer
{
    /// <summary>
    /// A hard stop on how many days one call may ask for. A range is only ever given by an
    /// administrator or computed from a window, so this is a guard against a typo — 400 days is
    /// more than a year, which is more than any screen shows.
    /// </summary>
    internal const int MaxDays = 400;

    /// <summary>
    /// Recomputes every bucket of <paramref name="day"/>, and answers how many rows the table
    /// ended up with for it.
    ///
    /// <para>
    /// <b>It does not save.</b> The callers are a consumer and a job that own more than these
    /// rows — the job advances nothing but logs a count, the consumer may be batching days — and a
    /// save per day would commit a half-finished window.
    /// </para>
    /// </summary>
    internal static async Task<int> RecomputeDayAsync(
        IntegrationDbContext db,
        TimeProvider clock,
        Guid tenantId,
        DateOnly day,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(clock);

        var from = day.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);
        var to = from.AddDays(1);

        var fromAt = new DateTimeOffset(from, TimeSpan.Zero);
        var toAt = new DateTimeOffset(to, TimeSpan.Zero);

        // The outcome configuration AS IT IS NOW. Read once per day rather than joined, because
        // tel_call stores the CODE and the flag lives on the configuration row: an institution
        // that corrects a mis-configured outcome wants last month's contact figures corrected
        // with it, which a value frozen onto the call at wrap-up time would not give them.
        var contactCodes = await db.TelCallOutcomes
            .IgnoreQueryFilters()
            .Where(o => o.TenantId == tenantId && o.CountsAsContact)
            .Select(o => o.Code)
            .ToListAsync(ct);

        var contact = contactCodes.ToHashSet(StringComparer.Ordinal);

        // Only the four columns the counting needs, and no numbers of any kind: this runs for
        // every agent of every tenant every night, and materialising encrypted blobs to count
        // them would be the one place telephony statistics could put a phone number in a log.
        //
        // Internal calls are excluded HERE rather than filtered afterwards — TEL-B-19 says so,
        // and an exclusion written into the only query there is cannot be forgotten by a second
        // reader. A colleague-to-colleague call is work, but counting it would let an agent
        // consulting their manager all morning outrank one who spent it on clients.
        var rows = await db.TelCalls
            .IgnoreQueryFilters()
            .Where(c => c.TenantId == tenantId
                     && c.StartedAt >= fromAt
                     && c.StartedAt < toAt
                     && c.Direction != TelCallDirection.Internal)
            .Select(c => new CallFacts(
                c.CrmUserId,
                c.AgencyId,
                c.Direction,
                c.Status,
                c.OutcomeCode,
                c.TalkSeconds,
                c.RingSeconds))
            .ToListAsync(ct);

        var existing = await db.TelCallDailyStats
            .IgnoreQueryFilters()
            .AsTracking()
            .Where(s => s.TenantId == tenantId && s.StatDate == day)
            .ToListAsync(ct);

        var now = clock.GetUtcNow();

        var buckets = rows
            .GroupBy(c => (
                User: c.CrmUserId ?? TelCallDailyStat.Unattributed,
                Agency: c.AgencyId ?? TelCallDailyStat.Unattributed))
            .ToDictionary(g => g.Key, g => Count(g, contact));

        foreach (var (key, totals) in buckets)
        {
            var row = existing.FirstOrDefault(
                s => s.CrmUserId == key.User && s.AgencyId == key.Agency);

            if (row is null)
            {
                row = TelCallDailyStat.Create(tenantId, day, key.User, key.Agency, now);
                db.TelCallDailyStats.Add(row);
            }

            row.Set(totals, now);
        }

        // A bucket that no longer has calls is REMOVED, not zeroed. Its last call was
        // re-attributed to another agent, moved to another day by a corrected start instant, or
        // anonymised — and a row of noughts left behind would keep that agent in every list built
        // from this table, which is how a "most active agents" screen ends up naming somebody who
        // made no call.
        var stale = existing
            .Where(s => !buckets.ContainsKey((s.CrmUserId, s.AgencyId)))
            .ToList();

        if (stale.Count > 0)
            db.TelCallDailyStats.RemoveRange(stale);

        return buckets.Count;
    }

    /// <summary>
    /// Recomputes an inclusive range of days, oldest first, and saves once.
    ///
    /// <para>
    /// <b>One save for the whole range</b>, so a window is either entirely recomputed or entirely
    /// untouched. A partial window is the state that makes a screen show a trough that was never
    /// real, and somebody then goes looking for the week the branch stopped calling.
    /// </para>
    /// </summary>
    internal static async Task<StatsRecomputeReport> RecomputeRangeAsync(
        IntegrationDbContext db,
        TimeProvider clock,
        ILogger logger,
        Guid tenantId,
        DateOnly from,
        DateOnly to,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(logger);

        if (to < from) (from, to) = (to, from);

        var days = to.DayNumber - from.DayNumber + 1;

        if (days > MaxDays)
        {
            // Refused rather than truncated: a caller who asked for ten years and silently got
            // the first four hundred days would read the answer as complete.
            logger.LogWarning(
                "Telephony stats recomputation refused for tenant {TenantId}: {Days} day(s) "
                + "asked for, {Max} is the ceiling.", tenantId, days, MaxDays);

            return new StatsRecomputeReport(0, 0, Refused: true);
        }

        var buckets = 0;

        for (var day = from; day <= to; day = day.AddDays(1))
            buckets += await RecomputeDayAsync(db, clock, tenantId, day, ct);

        await db.SaveChangesAsync(ct);

        return new StatsRecomputeReport(days, buckets, Refused: false);
    }

    private static TelCallDailyTotals Count(IEnumerable<CallFacts> calls, HashSet<string> contact)
    {
        int inbound = 0, outbound = 0, answered = 0, missed = 0, failed = 0;
        int contacts = 0, wrappedUp = 0, talk = 0, ring = 0;

        foreach (var call in calls)
        {
            if (call.Direction == TelCallDirection.Inbound) inbound++;
            else outbound++;

            switch (call.Status)
            {
                // Counted as answered while still in progress: a screen refreshed at eleven must
                // show the call an agent is on, or the figure disagrees with the handset in their
                // hand. The recomputation of that day will see it Completed later anyway.
                case TelCallStatus.Completed:
                case TelCallStatus.InProgress:
                    answered++;
                    break;

                case TelCallStatus.Missed:
                case TelCallStatus.Voicemail:
                    missed++;
                    break;

                case TelCallStatus.Failed:
                    failed++;
                    break;

                // Ringing is in none of the three. It is a call in flight, not yet an outcome of
                // any kind, and putting it in "missed" would have the follow-up figure flicker
                // upward every time a telephone rings.
                default:
                    break;
            }

            if (call.OutcomeCode is { } code)
            {
                wrappedUp++;

                if (contact.Contains(code)) contacts++;
            }

            talk += call.TalkSeconds ?? 0;
            ring += call.RingSeconds ?? 0;
        }

        return new TelCallDailyTotals(
            inbound, outbound, answered, missed, failed, contacts, wrappedUp, talk, ring);
    }

    /// <summary>
    /// The projection the counting works from — four discriminators and two durations, chosen so
    /// no encrypted column is ever read by this feature.
    /// </summary>
    private sealed record CallFacts(
        Guid? CrmUserId,
        Guid? AgencyId,
        TelCallDirection Direction,
        TelCallStatus Status,
        string? OutcomeCode,
        int? TalkSeconds,
        int? RingSeconds);
}

/// <summary>
/// What one recomputation did. <paramref name="Refused"/> is told apart from "nothing to do"
/// because a range over the ceiling writes nothing and must not read as a successful empty run.
/// </summary>
internal sealed record StatsRecomputeReport(int Days, int Buckets, bool Refused);
