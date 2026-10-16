namespace Sankore.Modules.Integration.Domain;

using Sankore.Shared.Kernel;

/// <summary>
/// One day of one agent's telephony, in one agency — the read model behind the indicators
/// (TEL-B-19).
///
/// <para>
/// <b>It is a PROJECTION and nothing depends on it.</b> Every figure here can be derived again
/// from <c>tel_call</c>, which is the record of truth, and the recomputation is the only way these
/// rows are ever written — see <c>TelCallStatsRecomputer</c>. Deleting the whole table costs one
/// job run, which is exactly the property TEL-B-19 asks for when it says the model must be
/// recalculable.
/// </para>
///
/// <para>
/// <b>Why counters are recomputed and never incremented.</b> An incremented counter is the obvious
/// design and it is the wrong one: the moment it disagrees with the journal — a webhook replayed,
/// a call re-qualified, a deployment that missed three events — there is no way back. Resetting it
/// to zero and replaying history requires the history to be replayable, which it is not: the
/// outbox is not an event store. Recomputing a day from the calls of that day needs nothing but
/// the calls, costs one indexed <c>GROUP BY</c>, and gives the same answer whether it runs once or
/// a hundred times.
/// </para>
///
/// <para>
/// <b>Why the key columns hold <c>Guid.Empty</c> instead of null.</b> The row is identified by
/// (tenant, day, agent, agency) and PostgreSQL treats each null as distinct, so a unique index
/// over a nullable agency would constrain nothing and the same bucket could be inserted twice per
/// run. The repo settled this once already, for per-person questionnaires, and the rule it wrote
/// down is to store a real value rather than null in the columns of a unique key.
/// <see cref="Unattributed"/> is that value, with the same meaning in both columns: a call nobody
/// is attributed, or an agent in no agency. The API edge turns it back into null, because
/// <c>Guid.Empty</c> is not something a client should ever have to recognise.
/// </para>
/// </summary>
public sealed class TelCallDailyStat : AggregateRoot
{
    /// <summary>
    /// The "no agent" / "no agency" bucket. Not an identifier — a bucket, and the only sentinel
    /// in this schema.
    /// </summary>
    public static readonly Guid Unattributed = Guid.Empty;

    private TelCallDailyStat()
    {
    }

    public Guid Id { get; private set; }

    /// <summary>
    /// The day, as a date in UTC. <b>UTC and not the tenant's local day</b>, which is a real
    /// limitation and is stated rather than hidden: a call at 00:30 in Abidjan is the same day in
    /// both, but one at 23:30 in a UTC+2 tenant falls into the previous UTC day. The alternative
    /// — a per-tenant time zone applied here — would make every row's meaning depend on a setting
    /// that can change, so yesterday's figures would move when somebody edited it. The journal
    /// keeps the instant; this model keeps a UTC day; a screen that needs local days asks for the
    /// range it wants.
    /// </summary>
    public DateOnly StatDate { get; private set; }

    /// <summary>The agent, or <see cref="Unattributed"/>.</summary>
    public Guid CrmUserId { get; private set; }

    /// <summary>The agency the calls belonged to at the time, or <see cref="Unattributed"/>.</summary>
    public Guid AgencyId { get; private set; }

    public int InboundCount { get; private set; }

    public int OutboundCount { get; private set; }

    /// <summary>
    /// Calls that were picked up — <c>Completed</c> or <c>InProgress</c>. Counted on the STATUS
    /// and not on a non-null talk duration, because a provider that reports a conversation
    /// without its length still had one.
    /// </summary>
    public int AnsweredCount { get; private set; }

    /// <summary>
    /// <c>Missed</c> and <c>Voicemail</c> together, because the agent's follow-up list already
    /// treats them as one thing (TEL-B-13) and two columns that are always read added would be
    /// two columns to keep in step.
    /// </summary>
    public int MissedCount { get; private set; }

    /// <summary>Technical failures — an invalid number, an operator refusal.</summary>
    public int FailedCount { get; private set; }

    /// <summary>
    /// Calls qualified with an outcome whose configuration counts it as contact (TEL-B-11). The
    /// flag is read from <c>tel_call_outcome</c> AS IT IS NOW, so changing a code's meaning
    /// changes the history on the next recomputation — which is the behaviour an institution
    /// correcting a mis-configured outcome wants, and the opposite of what a frozen copy would
    /// give them.
    /// </summary>
    public int ContactCount { get; private set; }

    /// <summary>Calls with an outcome recorded at all, contact or not.</summary>
    public int WrappedUpCount { get; private set; }

    /// <summary>
    /// Total conversation time. Seconds and not minutes: minutes rounded per call and then summed
    /// drift by up to half a minute per call, and this column is what an average is divided out
    /// of.
    /// </summary>
    public int TalkSeconds { get; private set; }

    /// <summary>Total time spent ringing, answered or not.</summary>
    public int RingSeconds { get; private set; }

    /// <summary>When this row was last derived. The only non-derived column.</summary>
    public DateTimeOffset ComputedAt { get; private set; }

    /// <summary>
    /// A fresh bucket. <paramref name="crmUserId"/> and <paramref name="agencyId"/> are
    /// normalised: a null becomes <see cref="Unattributed"/>, so a caller cannot create the
    /// second shape of the same bucket.
    /// </summary>
    public static TelCallDailyStat Create(
        Guid tenantId, DateOnly statDate, Guid? crmUserId, Guid? agencyId, DateTimeOffset now)
    {
        if (tenantId == Guid.Empty) throw new DomainException("TenantId is required.");

        return new TelCallDailyStat
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            StatDate = statDate,
            CrmUserId = crmUserId ?? Unattributed,
            AgencyId = agencyId ?? Unattributed,
            ComputedAt = now,
        };
    }

    /// <summary>
    /// Replaces every figure. <b>Replaces, never adds</b> — this is what makes a second run of the
    /// same day a no-op instead of a doubling, and it is the reason the recomputer may be called
    /// by three different triggers without any of them knowing about the others.
    /// </summary>
    public void Set(TelCallDailyTotals totals, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(totals);

        InboundCount = totals.InboundCount;
        OutboundCount = totals.OutboundCount;
        AnsweredCount = totals.AnsweredCount;
        MissedCount = totals.MissedCount;
        FailedCount = totals.FailedCount;
        ContactCount = totals.ContactCount;
        WrappedUpCount = totals.WrappedUpCount;
        TalkSeconds = totals.TalkSeconds;
        RingSeconds = totals.RingSeconds;
        ComputedAt = now;
    }

    /// <summary>
    /// True when every figure is zero — a bucket whose last call was re-attributed to somebody
    /// else, or anonymised. The recomputer deletes these rather than keeping a row of noughts,
    /// because a list of agents built from this table would otherwise keep naming an agent who
    /// made no call that day.
    /// </summary>
    public bool IsEmpty
        => InboundCount == 0 && OutboundCount == 0 && AnsweredCount == 0 && MissedCount == 0
           && FailedCount == 0 && ContactCount == 0 && WrappedUpCount == 0
           && TalkSeconds == 0 && RingSeconds == 0;
}

/// <summary>
/// The figures of one bucket, as the recomputation produces them. A record and not the entity, so
/// the counting can be unit-tested — and so <c>Set</c> cannot be given a half-filled bucket.
/// </summary>
public sealed record TelCallDailyTotals(
    int InboundCount,
    int OutboundCount,
    int AnsweredCount,
    int MissedCount,
    int FailedCount,
    int ContactCount,
    int WrappedUpCount,
    int TalkSeconds,
    int RingSeconds);
