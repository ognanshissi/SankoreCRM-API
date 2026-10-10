namespace Sankore.Modules.Leads.Domain;

using Sankore.Shared.Kernel;

/// <summary>
/// Records a prospect's explicit consent for a specific purpose,
/// the channel it was obtained through, supporting evidence, and
/// its lifecycle (Active → Withdrawn).
/// </summary>
public sealed class LeadConsent : ITenant
{
    public Guid Id { get; private set; }
    public Guid TenantId { get; private set; }
    public Guid LeadId { get; private set; }

    /// <summary>Purpose for which consent was collected.</summary>
    public ConsentType Type { get; private set; }

    /// <summary>
    /// Whose consent this is, for a consent given PER PERSON (BNF-B-09) — a health declaration is
    /// consented to by the individual it is about, not once for the whole lead.
    ///
    /// <para>
    /// Null for every consent type that is lead-wide (marketing, contact channels), which is all of
    /// them except <see cref="ConsentType.HealthData"/>. Null and not "Lead": these rows predate
    /// the column, and backfilling a value onto consents that have no subject would assert
    /// something nobody recorded.
    /// </para>
    /// </summary>
    public QualificationSubjectType? SubjectType { get; private set; }

    /// <summary>The person the consent is about. Null together with <see cref="SubjectType"/>.</summary>
    public Guid? SubjectId { get; private set; }

    /// <summary>Channel / medium through which consent was obtained.</summary>
    public ConsentChannel Channel { get; private set; }

    public ConsentStatus Status { get; private set; }

    /// <summary>When consent was recorded (UTC).</summary>
    public DateTimeOffset GrantedAt { get; private set; }

    /// <summary>
    /// Proof reference: URL to a recorded call, signed document URL,
    /// web-form submission ID, IP address, or any auditable identifier.
    /// </summary>
    public string? ProofReference { get; private set; }

    /// <summary>
    /// The AUTHENTICATED USER who recorded this entry. An audit fact, never caller-supplied —
    /// it answers "who collected this consent", which for a health consent is the first thing a
    /// regulator asks.
    /// </summary>
    public Guid RecordedBy { get; private set; }

    /// <summary>
    /// The PERSON who gave the consent, when that is not the subject (BNF-B-09 names it
    /// <c>GivenBy</c>) — a parent consenting for a minor child.
    ///
    /// <para>
    /// Deliberately a separate field from <see cref="RecordedBy"/>, and the two must never be
    /// collapsed: one is the agent at the counter, the other is the person whose consent this is.
    /// Writing the giver into the recorder erases the agent from a compliance record, which is
    /// exactly the integrity defect that makes a consent unusable as evidence.
    /// </para>
    ///
    /// <para>
    /// Null when the subject consented for themselves. Validated by the caller against the
    /// household — an arbitrary id here would assert that somebody nobody can identify gave
    /// consent on a child's behalf.
    /// </para>
    /// </summary>
    public Guid? GivenBy { get; private set; }

    // ── Withdrawal ────────────────────────────────────────────────────────

    public DateTimeOffset? WithdrawnAt { get; private set; }
    public Guid? WithdrawnBy { get; private set; }
    public string? WithdrawalReason { get; private set; }

    private LeadConsent() { } // EF Core

    public static LeadConsent Create(
        Guid tenantId,
        Guid leadId,
        ConsentType type,
        ConsentChannel channel,
        Guid recordedBy,
        TimeProvider clock,
        string? proofReference = null,
        QualificationSubjectType? subjectType = null,
        Guid? subjectId = null,
        Guid? givenBy = null)
        => new()
        {
            SubjectType    = subjectType,
            SubjectId      = subjectId,
            GivenBy        = givenBy,
            Id             = Guid.NewGuid(),
            TenantId       = tenantId,
            LeadId         = leadId,
            Type           = type,
            Channel        = channel,
            Status         = ConsentStatus.Active,
            GrantedAt      = clock.GetUtcNow(),
            ProofReference = proofReference?.Trim(),
            RecordedBy     = recordedBy
        };

    /// <summary>
    /// Withdraws this consent. Idempotent — returns a failure if already withdrawn.
    /// </summary>
    /// <summary>
    /// Blanks the identifying content, keeping the row (BNF-B-19).
    /// </summary>
    /// <remarks>
    /// <b>The proof reference goes; the fact of the consent stays.</b> The type, the channel, the
    /// status and the dates are what prove a consent was obtained — which is precisely the record
    /// a regulator asks for, and the part an anonymisation must not destroy. The proof reference
    /// points at stored evidence that names the person, and `GivenBy` names them directly.
    /// </remarks>
    internal void Anonymize()
    {
        ProofReference   = null;
        GivenBy          = null;
        WithdrawalReason = null;
    }

    public Result Withdraw(Guid withdrawnBy, TimeProvider clock, string? reason = null)
    {
        if (Status == ConsentStatus.Withdrawn)
            return Result.Fail("CONSENT_ALREADY_WITHDRAWN");

        Status           = ConsentStatus.Withdrawn;
        WithdrawnAt      = clock.GetUtcNow();
        WithdrawnBy      = withdrawnBy;
        WithdrawalReason = reason?.Trim();
        return Result.Ok();
    }
}
