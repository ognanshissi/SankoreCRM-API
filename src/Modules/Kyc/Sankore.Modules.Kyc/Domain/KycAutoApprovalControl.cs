namespace Sankore.Modules.Kyc.Domain;

using Sankore.Shared.Kernel;

/// <summary>Where a sampled automatic approval stands in the compliance queue.</summary>
public enum KycAutoApprovalControlStatus
{
    /// <summary>Drawn, waiting for an officer to look at it.</summary>
    Pending,

    /// <summary>Looked at: the machine was right to approve.</summary>
    Compliant,

    /// <summary>
    /// Looked at: it should not have been approved without a human. A review is raised on the
    /// file, which is the only thing that can still be done about a relationship already entered
    /// into.
    /// </summary>
    NonCompliant
}

/// <summary>
/// One sampled automatic approval, and an officer's verdict on it (KRM-B-11).
///
/// <para>
/// <b>Why this exists at all.</b> Every other KYC file in this platform passes under at least two
/// pairs of human eyes. An automatic approval replaces both with a decision by the SYSTEM account,
/// and the specification's own compliance note says what the price of that is: a sampled
/// after-the-fact control, so an institution can answer « nous gardons la maîtrise du dispositif »
/// with rows rather than with an assurance. This table IS that answer.
/// </para>
///
/// <para>
/// <b>Its own entity and not a flag on <see cref="KycFile"/>.</b> Three reasons, and the third is
/// the one that decides it: a file can be approved automatically more than once over a lifetime
/// (a review re-opens it), so a column would be overwritten; the queue is read by a different
/// person with a different permission, and a predicate on a column means a filtered index on the
/// module's hottest table; and the verdict carries its own author, date and finding, which is
/// evidence in its own right — a regulator asks who checked, not only whether somebody did.
/// </para>
///
/// <para>
/// <b>What it deliberately does NOT carry.</b> No score, no flag, no field read on the document.
/// The officer opens the file, where those sit behind the permissions that guard them, and a
/// compliance queue is exactly the kind of list that gets exported and pasted into a ticket. What
/// it does keep is the POLICY VERSION the decision was taken under — the question a regulator
/// actually asks is "under which rules was this relationship entered into without a human", and
/// that answer must survive the rules changing.
/// </para>
/// </summary>
public sealed class KycAutoApprovalControl : AggregateRoot
{
    public Guid Id { get; private set; }
    public Guid KycFileId { get; private set; }

    /// <summary>
    /// The customer, denormalised. Carried so the queue can resolve a display name in one batch
    /// through <c>ICustomersModule</c> without joining a second schema — the same reason
    /// <see cref="KycRemoteInvitation.CustomerId"/> is on the invitation.
    /// </summary>
    public Guid CustomerId { get; private set; }

    public KycAutoApprovalControlStatus Status { get; private set; }

    /// <summary>The tier the machine granted. <c>Simplified</c> or <c>Full</c>, never None.</summary>
    public KycTier GrantedTier { get; private set; }

    /// <summary>
    /// Which version of <c>KycAutoApprovalPolicy</c>'s conditions approved the file. A number and
    /// not a date: the rules are code, they are deployed, and an officer reviewing a six-month-old
    /// approval needs to know it was taken under v1 even though the service now runs v3.
    /// </summary>
    public int PolicyVersion { get; private set; }

    public DateTimeOffset SampledAt { get; private set; }

    public Guid? ReviewedBy { get; private set; }
    public DateTimeOffset? ReviewedAt { get; private set; }

    /// <summary>
    /// What the officer found, in their own words. Mandatory on a NON-COMPLIANT verdict and
    /// optional on a compliant one: "I looked and it was fine" needs no paragraph, while "this
    /// should not have been approved" is the sentence the review it raises will be read against.
    ///
    /// <para>
    /// Stored in clear and shown to operators, so it must never carry a document number, a date of
    /// birth or an address — the same rule as <see cref="KycReviewSchedule.Reason"/>, and the UI
    /// labels the field accordingly.
    /// </para>
    /// </summary>
    public string? Finding { get; private set; }

    /// <summary>
    /// The review raised when the verdict was non-compliant, so the queue can link to it and
    /// nobody raises a second one by hand. Null on every other row.
    /// </summary>
    public Guid? RaisedReviewId { get; private set; }

    private KycAutoApprovalControl() { }

    /// <summary>
    /// Draws a file into the queue. Called from the automatic approval itself and from nowhere
    /// else: the sample is of approvals, and drawing later would mean drawing from the files that
    /// happen still to exist.
    /// </summary>
    public static KycAutoApprovalControl Sample(
        Guid tenantId,
        Guid kycFileId,
        Guid customerId,
        KycTier grantedTier,
        int policyVersion,
        TimeProvider clock)
    {
        if (tenantId == Guid.Empty) throw new DomainException("TenantId is required.");
        if (kycFileId == Guid.Empty) throw new DomainException("KycFileId is required.");

        // None would mean "a control of an approval that granted nothing", which is not a state the
        // automatic path can produce — ApproveAutomatically refuses it outright.
        if (grantedTier == KycTier.None)
            throw new DomainException("A sampled approval always granted a tier.");

        return new KycAutoApprovalControl
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            KycFileId = kycFileId,
            CustomerId = customerId,
            Status = KycAutoApprovalControlStatus.Pending,
            GrantedTier = grantedTier,
            PolicyVersion = policyVersion,
            SampledAt = clock.GetUtcNow(),
        };
    }

    /// <summary>
    /// Records the officer's verdict.
    ///
    /// <para>
    /// <b>Once.</b> A recorded finding is evidence of a control having been performed, and
    /// re-recording it would rewrite who found what — the same reason
    /// <see cref="KycApprovalStep.Decide"/> refuses a second decision. An officer who changes their
    /// mind raises a review on the file, which is the act that actually does something.
    /// </para>
    /// </summary>
    /// <param name="reviewedBy">
    /// The officer. <c>Guid.Empty</c> is refused and throws rather than failing: a control
    /// attributed to SYSTEM would be the system auditing itself, which is the one thing this table
    /// exists to make impossible.
    /// </param>
    public Result Record(
        bool compliant, Guid reviewedBy, string? finding, TimeProvider clock)
    {
        if (reviewedBy == Guid.Empty)
            throw new DomainException("An after-the-fact control requires a real officer.");

        if (Status != KycAutoApprovalControlStatus.Pending)
            return Result.Fail(KycErrors.AutoApprovalControlAlreadyRecorded);

        if (!compliant && string.IsNullOrWhiteSpace(finding))
            return Result.Fail(KycErrors.AutoApprovalControlFindingRequired);

        Status = compliant
            ? KycAutoApprovalControlStatus.Compliant
            : KycAutoApprovalControlStatus.NonCompliant;

        ReviewedBy = reviewedBy;
        ReviewedAt = clock.GetUtcNow();
        Finding = string.IsNullOrWhiteSpace(finding) ? null : finding.Trim();
        return Result.Ok();
    }

    /// <summary>
    /// Links the event-driven review a non-compliant verdict opened.
    ///
    /// <para>
    /// Separate from <see cref="Record"/> because the review is raised through its own command,
    /// after this row is written, and that command can fail — a control that is recorded without
    /// its review is a true statement about what happened, while a verdict rolled back because the
    /// review could not be scheduled would lose the finding itself.
    /// </para>
    /// </summary>
    public void LinkReview(Guid reviewId)
    {
        if (RaisedReviewId is null && reviewId != Guid.Empty)
            RaisedReviewId = reviewId;
    }
}
