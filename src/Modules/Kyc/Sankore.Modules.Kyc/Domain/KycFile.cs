namespace Sankore.Modules.Kyc.Domain;

using Sankore.Shared.Kernel;

/// <summary>
/// The root of a customer's KYC evidence: one open file per customer, carrying the status, the
/// tier in force and the confidence the verification produced.
///
/// <para>
/// Every status change goes through a method on this class, and every method returns
/// <see cref="Result"/> rather than throwing. The transition table is a compliance rule — an
/// invalid move must come back as <c>KYC_INVALID_TRANSITION</c> that a handler can report, not as
/// an exception that surfaces to an agent as a 500.
/// </para>
///
/// <para>
/// <see cref="CustomerId"/> is an OPAQUE reference to the customer record of M01. There is no
/// physical foreign key across module schemas, and this module never assumes what the id points at.
/// </para>
/// </summary>
public sealed class KycFile : AggregateRoot
{
    public Guid Id { get; private set; }

    /// <summary>Opaque reference to the customer record owned by M01.</summary>
    public Guid CustomerId { get; private set; }

    /// <summary>
    /// The agency the customer belongs to, copied from M01 when the file is opened.
    ///
    /// <para>
    /// Denormalised rather than resolved on read, and that is a correctness requirement, not a
    /// performance one: the agency perimeter has to be a SQL predicate. Resolving it per row and
    /// filtering afterwards would compute a list's total count and page boundaries BEFORE the
    /// perimeter applied — short pages and a lying counter.
    /// </para>
    ///
    /// <para>
    /// Nullable, and <c>null</c> means <b>visible only to an unrestricted caller</b>. It is the
    /// state of a file opened while M01 could not answer, and of every row that predates this
    /// column. Failing closed on visibility rather than on creation is deliberate: a transient
    /// hiccup in another module must not stop a customer from getting the KYC file they are
    /// entitled to, and must not hand their file to the wrong branch either.
    /// </para>
    /// </summary>
    public Guid? AgencyId { get; private set; }

    public KycFileStatus Status { get; private set; }
    public KycTier Tier { get; private set; }
    public KycChannel Channel { get; private set; }
    public KycVigilanceLevel VigilanceLevel { get; private set; }

    /// <summary>Latest global confidence score, 0-100. Null until a verification has run.</summary>
    public int? ConfidenceScore { get; private set; }
    public KycConfidenceLevel? ConfidenceLevel { get; private set; }

    /// <summary>
    /// Count of face-match attempts on this file. At or above the tenant's
    /// <c>face-match-max-attempts</c>, the approval circuit gains the branch manager whatever the
    /// vigilance level — so it is state of the file, not of a single verification.
    /// </summary>
    public int FaceMatchAttempts { get; private set; }

    /// <summary>Set when a document number collides with another open file of the tenant.</summary>
    public bool DuplicateSuspected { get; private set; }

    public DateOnly? NextReviewDate { get; private set; }
    public DateTimeOffset? ValidatedAt { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }
    public Guid CreatedBy { get; private set; }

    /// <summary>
    /// Who may not approve this file. Captured at creation because the approval rule is "never
    /// validate what you created", and the creator must stay knowable even after the agent has
    /// been reassigned or deactivated.
    /// </summary>
    public Guid? LastSubmittedBy { get; private set; }

    /// <summary>PostgreSQL xmin — optimistic concurrency, as in every other module.</summary>
    public uint Version { get; private set; }

    private KycFile() { }

    public static KycFile Open(
        Guid tenantId,
        Guid customerId,
        KycChannel channel,
        Guid createdBy,
        TimeProvider clock,
        KycVigilanceLevel vigilanceLevel = KycVigilanceLevel.Standard,
        Guid? id = null,
        Guid? agencyId = null)
    {
        if (tenantId == Guid.Empty) throw new DomainException("TenantId is required.");
        if (customerId == Guid.Empty) throw new DomainException("CustomerId is required.");

        var now = clock.GetUtcNow();

        return new KycFile
        {
            // The caller may supply the id so a command can publish an event referencing the file
            // before SaveChanges. Same reason Client.Create takes one in M01.
            Id = id ?? Guid.NewGuid(),
            TenantId = tenantId,
            CustomerId = customerId,
            Status = KycFileStatus.Collecting,
            Tier = KycTier.None,
            Channel = channel,
            VigilanceLevel = vigilanceLevel,
            // Guid.Empty is not an agency: a caller that passes it means "unknown", and storing it
            // would create a perimeter nobody belongs to while looking like a real value.
            AgencyId = agencyId == Guid.Empty ? null : agencyId,
            CreatedAt = now,
            UpdatedAt = now,
            CreatedBy = createdBy,
        };
    }

    // ── Transition table ────────────────────────────────────────────────────

    /// <summary>
    /// The compliance rule, in one place. A handler never compares statuses itself: it calls a
    /// method below, and the table decides. Keeping it declarative is what makes it reviewable
    /// against the BCEAO instruction without reading the whole aggregate.
    /// </summary>
    private static readonly Dictionary<KycFileStatus, KycFileStatus[]> Allowed = new()
    {
        [KycFileStatus.Collecting] = [KycFileStatus.Verifying],
        [KycFileStatus.Verifying] = [KycFileStatus.Validating, KycFileStatus.ComplementRequired],
        [KycFileStatus.Validating] =
        [
            KycFileStatus.Simplified, KycFileStatus.Full,
            KycFileStatus.ComplementRequired, KycFileStatus.Rejected
        ],
        [KycFileStatus.ComplementRequired] = [KycFileStatus.Verifying],
        [KycFileStatus.Simplified] = [KycFileStatus.Validating, KycFileStatus.UnderReview, KycFileStatus.Suspended],
        [KycFileStatus.Full] = [KycFileStatus.UnderReview, KycFileStatus.Simplified, KycFileStatus.Suspended],
        [KycFileStatus.UnderReview] =
        [
            KycFileStatus.Full, KycFileStatus.Simplified,
            KycFileStatus.Expired, KycFileStatus.Suspended
        ],
        [KycFileStatus.Expired] = [KycFileStatus.UnderReview],
        [KycFileStatus.Rejected] = [],
        [KycFileStatus.Suspended] = [],
    };

    public bool CanTransitionTo(KycFileStatus target)
        => Allowed.TryGetValue(Status, out var targets) && targets.Contains(target);

    /// <summary>True while the file still counts as the customer's open KYC file.</summary>
    public bool IsOpen => Status is not (KycFileStatus.Rejected or KycFileStatus.Suspended);

    private Result MoveTo(KycFileStatus target, TimeProvider clock)
    {
        if (!CanTransitionTo(target))
            return Result.Fail(KycErrors.InvalidTransition);

        var previous = Status;
        Status = target;
        UpdatedAt = clock.GetUtcNow();
        RaiseDomainEvent(new KycFileStatusChangedDomainEvent(Id, previous, target));
        return Result.Ok();
    }

    // ── Behaviour ───────────────────────────────────────────────────────────

    /// <summary>Collection is done; the biometric verification may run.</summary>
    public Result SubmitForVerification(Guid submittedBy, TimeProvider clock)
    {
        var moved = MoveTo(KycFileStatus.Verifying, clock);
        if (moved.IsFailure) return moved;

        LastSubmittedBy = submittedBy;
        return Result.Ok();
    }

    /// <summary>
    /// Records what the scoring service returned and routes the file accordingly: a rejected score
    /// goes back to the agent, anything else enters the approval circuit.
    /// </summary>
    public Result RecordVerification(int score, KycConfidenceLevel level, TimeProvider clock)
    {
        if (Status != KycFileStatus.Verifying)
            return Result.Fail(KycErrors.InvalidTransition);

        ConfidenceScore = Math.Clamp(score, 0, 100);
        ConfidenceLevel = level;

        return level == KycConfidenceLevel.Rejected
            ? MoveTo(KycFileStatus.ComplementRequired, clock)
            : MoveTo(KycFileStatus.Validating, clock);
    }

    /// <summary>
    /// Refreshes the confidence snapshot WITHOUT touching the status.
    ///
    /// <see cref="RecordVerification"/> cannot serve here: it also drives the state machine, and a
    /// field correction must not push a file into the approval circuit on its own. But leaving the
    /// snapshot stale is not an option either — every screen reads <see cref="ConfidenceScore"/>
    /// from the file, so an agent who fixed a misread name would keep seeing the score that the
    /// misread produced, and would correct it again.
    ///
    /// The assessment history stays authoritative; this is the cached head of it.
    /// </summary>
    public void RecordRescore(int score, KycConfidenceLevel level, TimeProvider clock)
    {
        ConfidenceScore = Math.Clamp(score, 0, 100);
        ConfidenceLevel = level;
        UpdatedAt = clock.GetUtcNow();
    }

    public Result RequestComplement(TimeProvider clock) => MoveTo(KycFileStatus.ComplementRequired, clock);

    /// <summary>
    /// Final approval. The tier decides which ceilings apply downstream, so it is set here and
    /// nowhere else.
    /// </summary>
    public Result Approve(KycTier tier, Guid approvedBy, TimeProvider clock, DateOnly? nextReviewDate = null)
    {
        if (tier == KycTier.None)
            return Result.Fail(KycErrors.InvalidTransition);

        // The four-eyes rule is owned here, not by the workflow engine. M01 learned the same thing
        // on client merges: the engine runs the circuit but enforces no self-approval, so the
        // module that cares has to check.
        if (LastSubmittedBy == approvedBy)
            return Result.Fail(KycErrors.SelfApprovalForbidden);

        var target = tier == KycTier.Simplified ? KycFileStatus.Simplified : KycFileStatus.Full;

        var moved = MoveTo(target, clock);
        if (moved.IsFailure) return moved;

        Tier = tier;
        ValidatedAt = clock.GetUtcNow();
        NextReviewDate = nextReviewDate;
        return Result.Ok();
    }

    public Result Reject(Guid rejectedBy, TimeProvider clock)
    {
        if (LastSubmittedBy == rejectedBy)
            return Result.Fail(KycErrors.SelfApprovalForbidden);

        return MoveTo(KycFileStatus.Rejected, clock);
    }

    public Result Suspend(TimeProvider clock) => MoveTo(KycFileStatus.Suspended, clock);

    public Result StartReview(TimeProvider clock) => MoveTo(KycFileStatus.UnderReview, clock);

    /// <summary>Grace period elapsed without a review: operations fall back to simplified caps.</summary>
    public Result Expire(TimeProvider clock) => MoveTo(KycFileStatus.Expired, clock);

    /// <summary>
    /// Downgrades an approved file to the simplified tier — an expired identity document, or a
    /// review that no longer supports the full tier. Not a rejection: the customer keeps a valid,
    /// capped relationship.
    /// </summary>
    public Result DowngradeToSimplified(TimeProvider clock)
    {
        var moved = MoveTo(KycFileStatus.Simplified, clock);
        if (moved.IsFailure) return moved;

        Tier = KycTier.Simplified;
        return Result.Ok();
    }

    public void SetVigilanceLevel(KycVigilanceLevel level, TimeProvider clock)
    {
        VigilanceLevel = level;
        UpdatedAt = clock.GetUtcNow();
    }

    public void FlagDuplicateSuspected(TimeProvider clock)
    {
        DuplicateSuspected = true;
        VigilanceLevel = KycVigilanceLevel.High;
        UpdatedAt = clock.GetUtcNow();
    }

    /// <summary>Lifting the flag is a compliance act and the caller must have recorded a reason.</summary>
    public void ClearDuplicateSuspicion(TimeProvider clock)
    {
        DuplicateSuspected = false;
        UpdatedAt = clock.GetUtcNow();
    }

    public void RecordFaceMatchAttempt(TimeProvider clock)
    {
        FaceMatchAttempts++;
        UpdatedAt = clock.GetUtcNow();
    }

    public void ScheduleNextReview(DateOnly due, TimeProvider clock)
    {
        NextReviewDate = due;
        UpdatedAt = clock.GetUtcNow();
    }
}

/// <summary>Raised on every status change so the slice can publish the matching integration event.</summary>
public sealed record KycFileStatusChangedDomainEvent(
    Guid KycFileId,
    KycFileStatus Previous,
    KycFileStatus Current) : DomainEventBase;
