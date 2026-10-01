namespace Sankore.Modules.Kyc.Domain;

using Sankore.Shared.Kernel;

/// <summary>Who must sign, in order. The ladder is chosen from the vigilance level.</summary>
public enum KycApprovalLevel
{
    Agent = 1,
    BranchManager = 2,
    ComplianceOfficer = 3
}

public enum KycApprovalDecision
{
    Pending,
    Approved,
    Rejected,
    ComplementRequired
}

/// <summary>
/// One rung of the approval circuit, with the decision taken on it.
///
/// Rows are created up-front for the whole circuit, in <see cref="Level"/> order, so the file can
/// show "waiting for the branch manager" without inferring it from what is missing. A step keeps
/// its decision, its author, its comment and its timestamp — BCEAO instruction n°003-03-2025 is
/// about being able to show who decided what, not only about the outcome.
/// </summary>
public sealed class KycApprovalStep : AggregateRoot
{
    public Guid Id { get; private set; }
    public Guid KycFileId { get; private set; }

    public KycApprovalLevel Level { get; private set; }

    /// <summary>
    /// <see cref="Level"/>'s position on the ladder, persisted as a number.
    ///
    /// <para>
    /// Not independent data: it is <c>(int)Level</c>, set by the only factory, so the two cannot
    /// disagree. It exists because <see cref="Level"/> is stored as TEXT — readable in psql during
    /// an audit, which is worth more than the bytes — and a text <c>ORDER BY</c> sorts the ladder
    /// alphabetically. That happens to agree with it today and stops agreeing the first time a
    /// level is renamed, which is why <c>DecideKycApprovalHandler</c> sorts in memory instead.
    /// </para>
    ///
    /// <para>
    /// In memory the enum already orders correctly; this column is what lets SQL do it. Without it,
    /// "the files whose next rung is mine" cannot be a predicate, so a dashboard could not put them
    /// first across a paged list — only inside the page it happened to fetch.
    /// </para>
    /// </summary>
    public int LevelRank { get; private set; }

    public KycApprovalDecision Decision { get; private set; }

    public Guid? ApproverId { get; private set; }
    public string? Comment { get; private set; }
    public DateTimeOffset? DecidedAt { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }

    private KycApprovalStep() { }

    public static KycApprovalStep Pending(
        Guid tenantId, Guid kycFileId, KycApprovalLevel level, TimeProvider clock)
        => new()
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            KycFileId = kycFileId,
            Level = level,
            LevelRank = (int)level,
            Decision = KycApprovalDecision.Pending,
            CreatedAt = clock.GetUtcNow(),
        };

    public Result Decide(
        KycApprovalDecision decision, Guid approverId, TimeProvider clock, string? comment = null)
    {
        if (decision == KycApprovalDecision.Pending)
            return Result.Fail(KycErrors.InvalidTransition);

        // A decided step is evidence. Re-deciding it would rewrite who signed what, which is the
        // one thing the circuit exists to make impossible.
        if (Decision != KycApprovalDecision.Pending)
            return Result.Fail(KycErrors.ApprovalStepAlreadyDecided);

        Decision = decision;
        ApproverId = approverId;
        Comment = comment;
        DecidedAt = clock.GetUtcNow();
        return Result.Ok();
    }
}

public enum KycReviewTrigger
{
    /// <summary>Scheduled from the risk level at validation time.</summary>
    Periodic,

    /// <summary>Raised by something that happened — a transaction alert, a new document.</summary>
    Event
}

public enum KycReviewStatus
{
    Scheduled,
    Due,
    Completed,
    Cancelled
}

/// <summary>
/// A planned KYC review. One row per occurrence, never updated in place for the next one: a file
/// must be able to show when it was last reviewed and when it was due, which a single mutable
/// due-date column cannot.
/// </summary>
public sealed class KycReviewSchedule : AggregateRoot
{
    public Guid Id { get; private set; }
    public Guid KycFileId { get; private set; }

    public DateOnly DueDate { get; private set; }
    public KycReviewTrigger Trigger { get; private set; }
    public KycReviewStatus Status { get; private set; }

    /// <summary>Why an event-driven review was raised. Never a sensitive value.</summary>
    public string? Reason { get; private set; }

    public DateTimeOffset? MarkedDueAt { get; private set; }
    public DateTimeOffset? CompletedAt { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }

    private KycReviewSchedule() { }

    public static KycReviewSchedule Schedule(
        Guid tenantId, Guid kycFileId, DateOnly dueDate, KycReviewTrigger trigger,
        TimeProvider clock, string? reason = null)
        => new()
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            KycFileId = kycFileId,
            DueDate = dueDate,
            Trigger = trigger,
            Status = KycReviewStatus.Scheduled,
            Reason = reason,
            CreatedAt = clock.GetUtcNow(),
        };

    public Result MarkDue(TimeProvider clock)
    {
        if (Status != KycReviewStatus.Scheduled)
            return Result.Fail(KycErrors.InvalidTransition);

        Status = KycReviewStatus.Due;
        MarkedDueAt = clock.GetUtcNow();
        return Result.Ok();
    }

    public Result Complete(TimeProvider clock)
    {
        if (Status is KycReviewStatus.Completed or KycReviewStatus.Cancelled)
            return Result.Fail(KycErrors.InvalidTransition);

        Status = KycReviewStatus.Completed;
        CompletedAt = clock.GetUtcNow();
        return Result.Ok();
    }

    /// <summary>
    /// The grace period runs from the due date, not from the day the job noticed: a job that did
    /// not run for a week must not hand every tenant a free extra week of non-compliance.
    /// </summary>
    public bool IsPastGrace(DateOnly today, int graceDays)
        => today > DueDate.AddDays(graceDays);
}
