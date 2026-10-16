namespace Sankore.Modules.Integration.Domain;

using Sankore.Shared.Kernel;

/// <summary>
/// One call, as SANKORE knows it (TEL-B-08).
///
/// <para>
/// <b>The provider is the authority, this row is the journal.</b> A webhook is a signal: it says
/// something happened and may arrive twice, late, or out of order. Everything final here — the
/// end, the durations, the recording — is written after re-reading the call from the provider's
/// API. The one exception is the inbound ring, which has to reach the agent's screen in the second
/// and therefore lands on nothing but a <see cref="TelCallStatus.Ringing"/> row.
/// </para>
///
/// <para>
/// <b>Both numbers are encrypted and blind-indexed.</b> A telephone number is personal data and
/// the index is the only thing a query may match on — which is also what lets an inbound caller
/// id be resolved to a record without the module ever holding a readable number.
/// </para>
/// </summary>
public sealed class TelCall : AggregateRoot
{
    private TelCall() { }

    public Guid Id { get; private set; }

    public Guid ConnectionId { get; private set; }

    /// <summary>
    /// The provider's own identifier. Unique per connection, which is what makes a redelivered
    /// webhook and a catch-up sync converge on one row instead of two.
    /// </summary>
    public string ExternalCallId { get; private set; } = default!;

    public TelCallDirection Direction { get; private set; }

    public TelCallStatus Status { get; private set; }

    public byte[] FromNumberEnc { get; private set; } = default!;
    public byte[] FromNumberBi { get; private set; } = default!;
    public byte[] ToNumberEnc { get; private set; } = default!;
    public byte[] ToNumberBi { get; private set; } = default!;

    /// <summary>The provider's user id. Null when the call came from an account nobody linked.</summary>
    public string? ExternalUserId { get; private set; }

    /// <summary>
    /// The CRM user, resolved through <c>tel_user_link</c>. Null is ordinary and not an error:
    /// an unlinked account's calls are journalled and listed under "calls to attribute".
    /// </summary>
    public Guid? CrmUserId { get; private set; }

    /// <summary>
    /// The agency this call belonged to — the one its agent sat in AT THE TIME, copied from
    /// asked of M12 when the row was opened (TEL-B-19).
    ///
    /// <para>
    /// <b>A copy and not a join, deliberately.</b> It is the axis the per-agency indicators
    /// aggregate on, and it is also what finally gives "is this call inside my perimeter" an
    /// answer — the question the supervisor correction of TEL-B-09 and the recording player of
    /// TEL-B-17 both needed and neither could ask. A join to the agent's CURRENT agency would
    /// answer a different question: an agent moving branch in March would move February's figures
    /// and February's conversations with them.
    /// </para>
    ///
    /// <para>
    /// Null for a call nobody is attributed, and for an agent with no agency. Null means "outside
    /// every restricted perimeter".
    /// </para>
    /// </summary>
    public Guid? AgencyId { get; private set; }

    /// <summary>
    /// The record this call is about — <c>Lead</c> or <c>Customer</c>. An opaque pair with no
    /// foreign key; both members are null together, which a check constraint enforces.
    /// </summary>
    public string? EntityType { get; private set; }

    public Guid? EntityId { get; private set; }

    public TelMatchStatus MatchStatus { get; private set; } = TelMatchStatus.Unknown;

    public TelMatchMethod? MatchMethod { get; private set; }

    /// <summary>The click-to-call request this call answers, when it answers one.</summary>
    public Guid? ClickToCallId { get; private set; }

    public DateTimeOffset StartedAt { get; private set; }
    public DateTimeOffset? AnsweredAt { get; private set; }
    public DateTimeOffset? EndedAt { get; private set; }

    /// <summary>
    /// Seconds of ringing and of conversation, as the PROVIDER counted them. Stored rather than
    /// derived on read: a provider's own figures are what an operator reconciles an invoice
    /// against, and recomputing them from our timestamps would drift by the latency of a webhook.
    /// </summary>
    public int? RingSeconds { get; private set; }

    public int? TalkSeconds { get; private set; }

    /// <summary>The tenant's own outcome code (<c>tel_call_outcome</c>), set at wrap-up.</summary>
    public string? OutcomeCode { get; private set; }

    /// <summary>The agent's note, encrypted. Never projected into a timeline summary.</summary>
    public byte[]? NoteEnc { get; private set; }

    public DateTimeOffset? WrappedUpAt { get; private set; }

    /// <summary>The agent asked for this one to be kept, under the <c>OnDemand</c> mode.</summary>
    public bool KeepRecording { get; private set; }

    public TelRecordingStatus RecordingStatus { get; private set; } = TelRecordingStatus.NotRecorded;

    /// <summary>
    /// PostgreSQL <c>xmin</c>. Load-bearing here more than anywhere else in this module: a
    /// webhook and the catch-up sync can reach the same row in the same second.
    /// </summary>
    public uint Version { get; private set; }

    /// <summary>
    /// Opens the row on the first thing we learn about a call — usually a ring, sometimes a
    /// completed call discovered by the catch-up sync.
    /// </summary>
    public static TelCall Open(
        Guid tenantId,
        Guid connectionId,
        string externalCallId,
        TelCallDirection direction,
        TelCallStatus status,
        byte[] fromNumberEnc,
        byte[] fromNumberBi,
        byte[] toNumberEnc,
        byte[] toNumberBi,
        DateTimeOffset startedAt,
        string? externalUserId = null,
        Guid? crmUserId = null,
        Guid? clickToCallId = null,
        Guid? agencyId = null)
    {
        if (tenantId == Guid.Empty) throw new DomainException("TenantId is required.");
        if (connectionId == Guid.Empty) throw new DomainException("ConnectionId is required.");

        if (string.IsNullOrWhiteSpace(externalCallId))
            throw new DomainException("A call must carry the provider's identifier.");

        return new TelCall
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            ConnectionId = connectionId,
            ExternalCallId = externalCallId.Trim(),
            Direction = direction,
            Status = status,
            FromNumberEnc = fromNumberEnc,
            FromNumberBi = fromNumberBi,
            ToNumberEnc = toNumberEnc,
            ToNumberBi = toNumberBi,
            StartedAt = startedAt,
            ExternalUserId = string.IsNullOrWhiteSpace(externalUserId) ? null : externalUserId.Trim(),
            CrmUserId = crmUserId,
            AgencyId = agencyId == Guid.Empty ? null : agencyId,
            ClickToCallId = clickToCallId,
            MatchStatus = TelMatchStatus.Unknown,
        };
    }

    /// <summary>
    /// The transitions of §4.1. A call that has ended has ended: the four terminal statuses
    /// accept nothing further.
    ///
    /// <para>
    /// <b><c>Ringing → Completed</c> is allowed, and leaving it out was a defect.</b> It looks
    /// wrong — a call cannot finish without having been answered — but the status here is not the
    /// call's history, it is the last thing the PROVIDER said. The "answered" event is the one
    /// most easily lost: it is a webhook, delivery is at-least-once and not at-most-once-in-order,
    /// and the authoritative re-read that follows a hang-up reports <c>Completed</c> outright. A
    /// table that refused it would leave the row saying <c>Ringing</c> for ever about a
    /// conversation the provider has fully described — and nothing would ever revisit it, because
    /// the terminal statuses accept nothing and this one is not terminal. The answer instant is
    /// not invented in that case: <see cref="RecordInstants"/> writes what the provider reports,
    /// and a provider that reports no answer time leaves it null.
    /// </para>
    /// </summary>
    private static readonly IReadOnlyDictionary<TelCallStatus, TelCallStatus[]> Allowed =
        new Dictionary<TelCallStatus, TelCallStatus[]>
        {
            [TelCallStatus.Ringing] =
            [
                TelCallStatus.InProgress,
                TelCallStatus.Completed,
                TelCallStatus.Missed,
                TelCallStatus.Voicemail,
                TelCallStatus.Failed,
            ],
            [TelCallStatus.InProgress] = [TelCallStatus.Completed],
            [TelCallStatus.Completed] = [],
            [TelCallStatus.Missed] = [],
            [TelCallStatus.Voicemail] = [],
            [TelCallStatus.Failed] = [],
        };

    /// <summary>
    /// Moves the call along. <b>Answers false instead of throwing</b> on a transition that is not
    /// allowed, and the caller logs it: the trigger is a provider's webhook, and handing an error
    /// back would make the provider retry an event that will never be accepted — for hours.
    /// Out-of-order delivery is ordinary, not exceptional.
    /// </summary>
    public bool TryMoveTo(TelCallStatus next, DateTimeOffset at)
    {
        if (Status == next) return false;
        if (!Allowed[Status].Contains(next)) return false;

        Status = next;

        if (next == TelCallStatus.InProgress) AnsweredAt ??= at;

        if (next is TelCallStatus.Completed or TelCallStatus.Missed
                 or TelCallStatus.Voicemail or TelCallStatus.Failed)
        {
            EndedAt ??= at;
        }

        return true;
    }

    /// <summary>
    /// Fills in the agency when it could not be resolved at creation (TEL-B-19).
    ///
    /// <para>
    /// <b>Write-once, and that is the point.</b> M12 being unreachable during a ring would
    /// otherwise leave the call outside every agency for ever — so it is retried on EVERY journal
    /// reading of a row that still has none, and a call gets two to four of those, which means
    /// the next webhook repairs it. Once a value is in, no later reading may change it: the row
    /// records the agency the conversation belonged to, and an agent transferred next month must
    /// not take this call with them.
    /// </para>
    /// </summary>
    public bool StampAgency(Guid? agencyId)
    {
        if (AgencyId is not null) return false;

        var resolved = agencyId == Guid.Empty ? null : agencyId;

        if (resolved is null) return false;

        AgencyId = resolved;

        return true;
    }

    /// <summary>
    /// The instants the provider reports, for a call that was never walked through the
    /// transitions — the catch-up sweep discovering a call that already finished.
    ///
    /// <para>
    /// <c>??=</c> on both, like <see cref="TryMoveTo"/>: the first reading of an instant is the
    /// one closest to the event, and a later re-read moving it would make every duration on the
    /// screen disagree with the provider's own figures by the latency of whichever path arrived
    /// last. Nulls are ignored — a provider that stops reporting an answer time must not erase
    /// one already recorded.
    /// </para>
    /// </summary>
    public void RecordInstants(DateTimeOffset? answeredAt, DateTimeOffset? endedAt)
    {
        if (answeredAt is not null) AnsweredAt ??= answeredAt;
        if (endedAt is not null) EndedAt ??= endedAt;
    }

    /// <summary>
    /// Writes the durations the provider counted. Separate from the transition because they
    /// arrive with the authoritative re-read, not with the event that closed the call.
    /// </summary>
    public void RecordDurations(int? ringSeconds, int? talkSeconds)
    {
        if (ringSeconds is >= 0) RingSeconds = ringSeconds;
        if (talkSeconds is >= 0) TalkSeconds = talkSeconds;
    }

    /// <summary>
    /// Attaches the call to a record, or records that nothing matched.
    ///
    /// <para>
    /// A <see cref="TelMatchMethod.Manual"/> attachment may always overwrite an automatic one —
    /// that is what correcting a mistake means — while an automatic one never overwrites a
    /// manual: a human looked at this call, and a later sweep must not undo their reading.
    /// </para>
    /// </summary>
    public Result Attach(
        TelMatchStatus status, TelMatchMethod? method, string? entityType, Guid? entityId)
    {
        if ((entityType is null) != (entityId is null))
            return Result.Fail(TelephonyErrors.EntityReferenceIncomplete);

        if (entityType is not null && !TelEntityTypes.IsKnown(entityType))
            return Result.Fail(TelephonyErrors.EntityTypeUnknown);

        if (MatchMethod == TelMatchMethod.Manual && method != TelMatchMethod.Manual)
            return Result.Fail(TelephonyErrors.ManualAttachmentWins);

        MatchStatus = status;
        MatchMethod = method;
        EntityType = entityType;
        EntityId = entityId;

        return Result.Ok();
    }

    /// <summary>Records the agent's outcome and note (TEL-B-11).</summary>
    public Result WrapUp(string outcomeCode, byte[]? noteEnc, DateTimeOffset at)
    {
        if (string.IsNullOrWhiteSpace(outcomeCode))
            return Result.Fail(TelephonyErrors.OutcomeRequired);

        if (EndedAt is null)
            return Result.Fail(TelephonyErrors.CallNotEnded);

        OutcomeCode = outcomeCode.Trim();
        NoteEnc = noteEnc;
        WrappedUpAt = at;

        return Result.Ok();
    }

    /// <summary>The agent asked for this call to be kept, under the <c>OnDemand</c> mode.</summary>
    public void MarkForRecording() => KeepRecording = true;

    public void SetRecordingStatus(TelRecordingStatus status) => RecordingStatus = status;

    /// <summary>
    /// Blanks the numbers and the note once the retention window closes (TEL-B-08) or the person
    /// is anonymised. The call itself STAYS: direction, durations and outcome are what the
    /// statistics are made of, and destroying them would turn a retention rule into a reporting
    /// outage.
    /// </summary>
    public void Anonymise()
    {
        // AgencyId, Direction, the durations and the outcome all STAY — see the summary. The
        // agency is a property of the institution, not of the person.
        FromNumberEnc = [];
        FromNumberBi = [];
        ToNumberEnc = [];
        ToNumberBi = [];
        NoteEnc = null;
    }
}
