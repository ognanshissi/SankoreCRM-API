namespace Sankore.Modules.Integration.Domain;

using Sankore.Shared.Kernel;

/// <summary>How the complaint reached the institution.</summary>
public enum ComplaintChannel
{
    /// <summary>At a counter, taken down by an agent.</summary>
    Branch,

    Telephone,

    Email,

    /// <summary>The customer's own digital channel — portal, USSD, mobile app.</summary>
    CustomerChannel,

    /// <summary>A letter. Slow, and still the one a regulator asks about.</summary>
    Post,

    /// <summary>Forwarded by the insurer, or by the Direction Nationale des Assurances.</summary>
    Authority
}

/// <summary>
/// What the complaint is about. A closed list rather than free text, because the règlement
/// 01/CIMA/2024 asks the mechanism to be reportable — and "how many complaints about
/// mis-selling" is a question a free-text subject line cannot answer.
/// </summary>
public enum ComplaintCategory
{
    /// <summary>Anything the list does not name. Reported as such, never used as a default dump.</summary>
    Other,

    /// <summary>The cover sold is not the cover the customer believes they bought.</summary>
    MisSelling,

    /// <summary>A premium debited wrongly, twice, or in the wrong amount.</summary>
    Premium,

    /// <summary>The handling of a claim — delay, refusal, the indemnity paid.</summary>
    ClaimHandling,

    /// <summary>The pre-contractual information, the notice, the consent collected.</summary>
    Information,

    /// <summary>Personal data: a correction refused, a use not consented to.</summary>
    DataProtection,

    /// <summary>The electronic channel itself — a signature that failed, a document never received.</summary>
    DigitalChannel
}

/// <summary>
/// Where a complaint has got to.
///
/// <para>
/// <c>Acknowledged</c> is a state and not a flag, because the règlement's mechanism is judged on
/// two separate deadlines: how fast the institution says it has RECEIVED the complaint, and how
/// fast it ANSWERS. Folding the first into "under review" would make the first deadline
/// unmeasurable.
/// </para>
/// </summary>
public enum ComplaintStatus
{
    /// <summary>Recorded. The acknowledgement is owed.</summary>
    Received,

    /// <summary>The customer has been told it is in hand. The answer is owed.</summary>
    Acknowledged,

    /// <summary>Handed to the insurer or to the Direction Nationale des Assurances.</summary>
    Escalated,

    /// <summary>Answered, whatever the answer was. Terminal.</summary>
    Resolved,

    /// <summary>
    /// Withdrawn by the customer, or outside the mechanism's scope. Terminal, and distinct from
    /// <see cref="Resolved"/>: a report that counted a withdrawal as an answer would overstate
    /// how many complaints the institution actually treated.
    /// </summary>
    Closed
}

/// <summary>
/// One customer complaint about an insurance product or its distribution — the
/// <i>mécanisme de traitement des réclamations</i> the règlement
/// n° 01/CIMA/PCMA/CE/SG/2024 requires of anybody distributing insurance by electronic means.
///
/// <para>
/// <b>Why this is the only genuinely new table of the insurance family.</b> Nothing else in the
/// schema could carry it. A complaint may name no policy at all (a prospect complaining about a
/// quote), may outlive the contract it is about, and has deadlines of its own that belong to
/// neither the policy nor the claim. The 2024 text asks for a MECHANISM, which means something
/// countable: how many arrived, through which channel, about what, how long each took.
/// </para>
///
/// <para>
/// <b>The timestamped opposable record of each step is the AUDIT trail, not a second log here.</b>
/// Every write to this aggregate goes through a MediatR command, so <c>AuditBehavior</c> records
/// it with its actor and its instant, and <c>audit.entries</c> is immutable at the database level —
/// PostgreSQL triggers, the <c>ImmutableEntries</c> migration, not a DBA's promise. A parallel
/// event table here would be a second account of the same facts, and the day the two disagreed
/// the opposable one would be whichever a lawyer reached first. What this row keeps is the handful
/// of instants a DEADLINE is computed from, because a report cannot join against an audit log per
/// complaint.
/// </para>
///
/// <para>
/// <b>Insurance only, and said so.</b> It lives in the <c>integration</c> schema under the
/// <c>ins_</c> prefix because the obligation it serves is CIMA's, about insurance distribution. A
/// general complaints register covering savings, credit and tontines is a different object with a
/// different regulator, and merging the two would put a CIMA deadline on a credit grievance.
/// </para>
/// </summary>
public sealed class InsuranceComplaint : AggregateRoot
{
    public Guid Id { get; private set; }

    /// <summary>
    /// Per tenant and per year, human-readable: <c>REC-2026-000042</c>. What the customer is given
    /// and what they quote back, so it is stored rather than rendered — a number computed from the
    /// row's position would change the day a complaint is deleted.
    /// </summary>
    public string Reference { get; private set; } = string.Empty;

    /// <summary>
    /// The insurer, when the complaint concerns one of its contracts. NULLABLE and no FK: a
    /// complaint about the institution's own advice names no insurer, and one about a quote names
    /// no contract either.
    /// </summary>
    public Guid? ConnectionId { get; private set; }

    /// <summary>
    /// Opaque reference to M01's customer. Required — the mechanism answers a person, and a
    /// complaint with nobody to answer is not a complaint.
    /// </summary>
    public Guid CrmCustomerId { get; private set; }

    /// <summary>The contract concerned, when there is one. Plain id, no FK — see the remarks.</summary>
    public Guid? PolicyId { get; private set; }

    /// <summary>The claim concerned, for a <see cref="ComplaintCategory.ClaimHandling"/> complaint.</summary>
    public Guid? ClaimId { get; private set; }

    public ComplaintChannel Channel { get; private set; }

    public ComplaintCategory Category { get; private set; }

    /// <summary>
    /// A one-line subject, in clear. Operator-facing and deliberately not the complaint itself:
    /// it appears in lists and in a regulator's report, so it must be readable without reaching
    /// for a key.
    /// </summary>
    public string Subject { get; private set; } = string.Empty;

    /// <summary>
    /// What the customer actually said, encrypted (AES-256-GCM, this module's key).
    ///
    /// <para>
    /// A grievance is free text about a named person's money, health or family, written by
    /// somebody who is angry. It is encrypted for the same reason a claim's account of a loss is,
    /// and <c>text</c> with no cap because the ciphertext grows with it. Nothing searches it.
    /// </para>
    /// </summary>
    public string? BodyEncrypted { get; private set; }

    public ComplaintStatus Status { get; private set; }

    public DateTimeOffset ReceivedAt { get; private set; }

    /// <summary>Who recorded it. An agent, or SYSTEM when it arrived through a digital channel.</summary>
    public Guid ReceivedBy { get; private set; }

    /// <summary>
    /// When the institution must have acknowledged receipt. Stored and not computed, because the
    /// period is configurable and a deadline recomputed from today's setting would move under a
    /// complaint already late.
    /// </summary>
    public DateTimeOffset AcknowledgementDueAt { get; private set; }

    /// <summary>When the institution must have answered. Same rule as the acknowledgement.</summary>
    public DateTimeOffset ResolutionDueAt { get; private set; }

    public DateTimeOffset? AcknowledgedAt { get; private set; }

    public Guid? AcknowledgedBy { get; private set; }

    public DateTimeOffset? EscalatedAt { get; private set; }

    /// <summary>
    /// Who it went to — the insurer's own complaints unit, or the Direction Nationale des
    /// Assurances. Operator-facing text, never a credential.
    /// </summary>
    public string? EscalatedTo { get; private set; }

    public DateTimeOffset? ClosedAt { get; private set; }

    public Guid? ClosedBy { get; private set; }

    /// <summary>
    /// The answer given, in clear. Operator- and regulator-facing: it is the institution's own
    /// position, written to be read back, and a controller asking "what did you answer" must not
    /// be handed a ciphertext. The agent writing it is told not to repeat the customer's own
    /// account in it.
    /// </summary>
    public string? Resolution { get; private set; }

    /// <summary>
    /// Whether the complaint was found justified. Separate from the status, because a complaint
    /// can be answered either way and "how many did we uphold" is the question a mechanism is
    /// judged on. Null while nothing has been decided.
    /// </summary>
    public bool? WasUpheld { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    /// <summary>
    /// PostgreSQL xmin. Carried here and not on the evidence tables because this row genuinely has
    /// several writers: an agent acknowledging while a supervisor escalates is ordinary, and the
    /// loser of that race must be told rather than silently overwritten.
    /// </summary>
    public uint Version { get; private set; }

    private InsuranceComplaint() { }

    public static InsuranceComplaint Create(
        Guid tenantId,
        string reference,
        Guid crmCustomerId,
        ComplaintChannel channel,
        ComplaintCategory category,
        string subject,
        Guid receivedBy,
        DateTimeOffset acknowledgementDueAt,
        DateTimeOffset resolutionDueAt,
        TimeProvider clock,
        Guid? connectionId = null,
        Guid? policyId = null,
        Guid? claimId = null,
        string? bodyEncrypted = null,
        DateTimeOffset? receivedAt = null,
        Guid? id = null)
    {
        if (tenantId == Guid.Empty) throw new DomainException("TenantId is required.");
        if (crmCustomerId == Guid.Empty)
            throw new DomainException(
                "A complaint needs the customer it answers; one with nobody to answer is not a "
                + "complaint.");
        if (string.IsNullOrWhiteSpace(reference))
            throw new DomainException("A complaint reference is required.");
        if (string.IsNullOrWhiteSpace(subject))
            throw new DomainException("A complaint needs a subject an operator can read.");
        ArgumentNullException.ThrowIfNull(clock);

        var now = clock.GetUtcNow();

        // The complaint may have arrived days before somebody typed it in — a letter, a call
        // logged late — and the deadlines run from when the CUSTOMER complained, not from when the
        // institution got round to recording it. A received date in the future is refused: that is
        // a typo, and honouring it would push both deadlines out.
        var received = receivedAt ?? now;

        if (received > now)
            throw new DomainException("A complaint cannot have been received in the future.");

        if (resolutionDueAt < acknowledgementDueAt)
            throw new DomainException(
                "The answer cannot be due before the acknowledgement of receipt.");

        return new InsuranceComplaint
        {
            Id = id ?? Guid.NewGuid(),
            TenantId = tenantId,
            Reference = reference.Trim(),
            ConnectionId = connectionId,
            CrmCustomerId = crmCustomerId,
            PolicyId = policyId,
            ClaimId = claimId,
            Channel = channel,
            Category = category,
            Subject = subject.Trim(),
            BodyEncrypted = bodyEncrypted,
            Status = ComplaintStatus.Received,
            ReceivedAt = received,
            ReceivedBy = receivedBy,
            AcknowledgementDueAt = acknowledgementDueAt,
            ResolutionDueAt = resolutionDueAt,
            CreatedAt = now,
            UpdatedAt = now,
        };
    }

    /// <summary>
    /// The institution has told the customer the complaint is in hand — the first of the two
    /// deadlines the mechanism is judged on.
    ///
    /// <para>
    /// Idempotent: acknowledging twice keeps the FIRST instant, because that is the one that was
    /// measured against the deadline. Re-stamping it would let a late acknowledgement be made to
    /// look punctual by sending a second one.
    /// </para>
    /// </summary>
    public Result Acknowledge(Guid actor, TimeProvider clock)
    {
        ArgumentNullException.ThrowIfNull(clock);

        if (Status is ComplaintStatus.Resolved or ComplaintStatus.Closed)
            return Result.Fail(ComplaintErrors.AlreadyClosed);

        if (AcknowledgedAt is not null) return Result.Ok();

        AcknowledgedAt = clock.GetUtcNow();
        AcknowledgedBy = actor;

        // Escalated stays escalated: a complaint handed to the insurer and only then acknowledged
        // to the customer has not come back to us.
        if (Status == ComplaintStatus.Received) Status = ComplaintStatus.Acknowledged;

        UpdatedAt = AcknowledgedAt.Value;

        return Result.Ok();
    }

    /// <summary>
    /// Handed on, to the insurer's own complaints unit or to the Direction Nationale des
    /// Assurances. Not terminal: the institution still owes the customer an answer, and the
    /// resolution deadline keeps running — which is the whole reason escalation is a status and
    /// not a closure.
    /// </summary>
    public Result Escalate(string to, Guid actor, TimeProvider clock)
    {
        ArgumentNullException.ThrowIfNull(clock);

        if (string.IsNullOrWhiteSpace(to))
            return Result.Fail(ComplaintErrors.EscalationTargetRequired);

        if (Status is ComplaintStatus.Resolved or ComplaintStatus.Closed)
            return Result.Fail(ComplaintErrors.AlreadyClosed);

        Status = ComplaintStatus.Escalated;
        EscalatedAt = clock.GetUtcNow();
        EscalatedTo = to.Trim();
        UpdatedAt = EscalatedAt.Value;

        return Result.Ok();
    }

    /// <summary>
    /// The answer, and whether the complaint was upheld. Terminal.
    ///
    /// <para>
    /// The answer text is REQUIRED, including for a complaint found unjustified: "we considered
    /// your complaint and rejected it" with no reason is exactly the answer the 2024 mechanism
    /// exists to prevent, and it is the one a regulator asks to see.
    /// </para>
    /// </summary>
    public Result Resolve(string resolution, bool wasUpheld, Guid actor, TimeProvider clock)
    {
        ArgumentNullException.ThrowIfNull(clock);

        if (string.IsNullOrWhiteSpace(resolution))
            return Result.Fail(ComplaintErrors.ResolutionRequired);

        if (Status is ComplaintStatus.Resolved or ComplaintStatus.Closed)
            return Result.Fail(ComplaintErrors.AlreadyClosed);

        Status = ComplaintStatus.Resolved;
        Resolution = resolution.Trim();
        WasUpheld = wasUpheld;
        ClosedAt = clock.GetUtcNow();
        ClosedBy = actor;
        UpdatedAt = ClosedAt.Value;

        return Result.Ok();
    }

    /// <summary>
    /// Withdrawn by the customer, or outside the mechanism. Terminal, and deliberately NOT
    /// <see cref="ComplaintStatus.Resolved"/>: a report counting a withdrawal as an answer would
    /// overstate how many complaints the institution treated.
    ///
    /// <para>
    /// <see cref="WasUpheld"/> stays null, because nothing was decided.
    /// </para>
    /// </summary>
    public Result Close(string reason, Guid actor, TimeProvider clock)
    {
        ArgumentNullException.ThrowIfNull(clock);

        if (string.IsNullOrWhiteSpace(reason))
            return Result.Fail(ComplaintErrors.ResolutionRequired);

        if (Status is ComplaintStatus.Resolved or ComplaintStatus.Closed)
            return Result.Fail(ComplaintErrors.AlreadyClosed);

        Status = ComplaintStatus.Closed;
        Resolution = reason.Trim();
        ClosedAt = clock.GetUtcNow();
        ClosedBy = actor;
        UpdatedAt = ClosedAt.Value;

        return Result.Ok();
    }

    /// <summary>True while the institution still owes the customer something.</summary>
    public bool IsOpen => Status is ComplaintStatus.Received
        or ComplaintStatus.Acknowledged
        or ComplaintStatus.Escalated;

    /// <summary>
    /// The acknowledgement is late. Computed from the STORED deadline, so it does not move when
    /// an administrator changes the period.
    /// </summary>
    public bool AcknowledgementOverdueAt(DateTimeOffset now)
        => AcknowledgedAt is null && IsOpen && AcknowledgementDueAt < now;

    /// <summary>The answer is late.</summary>
    public bool ResolutionOverdueAt(DateTimeOffset now)
        => IsOpen && ResolutionDueAt < now;
}

/// <summary>Error codes the aggregate returns. Stable UPPER_SNAKE.</summary>
public static class ComplaintErrors
{
    public const string NotFound = "INSURANCE_COMPLAINT_NOT_FOUND";

    /// <summary>
    /// The complaint is already answered or withdrawn. A conflict: re-answering would rewrite what
    /// the customer was told.
    /// </summary>
    public const string AlreadyClosed = "INSURANCE_COMPLAINT_ALREADY_CLOSED";

    /// <summary>
    /// An answer with no text. Refused even for a rejection — see <c>InsuranceComplaint.Resolve</c>.
    /// </summary>
    public const string ResolutionRequired = "INSURANCE_COMPLAINT_RESOLUTION_REQUIRED";

    public const string EscalationTargetRequired = "INSURANCE_COMPLAINT_ESCALATION_TARGET_REQUIRED";
}
