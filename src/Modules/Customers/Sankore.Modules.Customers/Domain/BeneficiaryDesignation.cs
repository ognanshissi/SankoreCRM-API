namespace Sankore.Modules.Customers.Domain;

using Sankore.Shared.Kernel;

/// <summary>
/// One person designated on a holding, in one role (BNF-B-15) — a death-benefit beneficiary, a
/// health dependent, a co-holder, a proxy.
///
/// <para>
/// A child of the <see cref="ProductHolding"/> aggregate, reached only through it, because every
/// invariant is about the SET: the cardinality of a role, and the shares of a rank summing to
/// 100%. A standalone entity would leave both to whichever handler happened to write.
/// </para>
///
/// <para>
/// <b>The designated person is an opaque pair</b>, not a foreign key:
/// <see cref="SubjectType"/>/<see cref="SubjectId"/> point either at a client of the tenant, at a
/// <c>ClientRelationship</c> row (a household member who is not a client), or at nobody at all —
/// a named third party who is neither. The same person may be designated on several holdings
/// (BNF-B-15 is explicit), so nothing here is unique per subject.
/// </para>
/// </summary>
public sealed class BeneficiaryDesignation
{
    public Guid Id { get; private set; }
    public Guid TenantId { get; private set; }
    public Guid HoldingId { get; private set; }

    /// <summary>
    /// The role's CODE as the product line declares it — upper-cased
    /// (<c>DesignationRoleDefinition.Code</c>). A string and not an enum: BNF-B-06 makes the list
    /// per-line and administrator-configurable, so an enum would mean a deploy whenever an
    /// institution invents a role.
    /// </summary>
    public string RoleCode { get; private set; } = default!;

    /// <summary>"Client" | "HouseholdMember" | "ExternalPerson". See the class remarks.</summary>
    public DesignationSubjectType SubjectType { get; private set; }

    /// <summary>
    /// The client id or the relationship id. <c>null</c> for an external person, whose identity is
    /// carried by the name fields below — and a null here is why the subject cannot be a key.
    /// </summary>
    public Guid? SubjectId { get; private set; }

    /// <summary>Who they are, for a subject the tenant holds no record of. Null otherwise.</summary>
    public string? ExternalFullName { get; private set; }

    /// <summary>Their relationship to the holder, in the institution's own words.</summary>
    public string? ExternalRelationship { get; private set; }

    /// <summary>
    /// The rank this designation sits in (BNF-B-15): 1 for the first-rank beneficiaries, 2 for
    /// those who inherit only if rank 1 cannot. Shares sum to 100% WITHIN a rank, never across.
    /// </summary>
    public int Rank { get; private set; }

    /// <summary>
    /// The quote-part in percent, 0–100, or null for a role where a share means nothing (a proxy,
    /// a co-holder). Stored with two decimals: thirds of a capital are 33.33 / 33.33 / 33.34.
    /// </summary>
    public decimal? SharePercent { get; private set; }

    public DesignationStatus Status { get; private set; }

    /// <summary>
    /// Why this designation needs review, or why it was closed. Required to close one: a
    /// designation that vanished without a reason is unauditable.
    /// </summary>
    public string? StatusReason { get; private set; }

    // ── Versioning (BNF-B-15) ────────────────────────────────────────────────

    /// <summary>
    /// Which version of the clause this row belongs to. On an <c>Active</c> holding a change does
    /// not edit a designation, it writes a NEW version and closes the previous one — an avenant.
    /// </summary>
    public int ClauseVersion { get; private set; }

    public DateTimeOffset ValidFrom { get; private set; }

    /// <summary>Null while the designation stands. Set when a later version supersedes it.</summary>
    public DateTimeOffset? ValidTo { get; private set; }

    public Guid RecordedBy { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }

    public bool IsCurrent => ValidTo is null && Status != DesignationStatus.Closed;

    private BeneficiaryDesignation() { } // EF Core

    internal static BeneficiaryDesignation Create(
        Guid tenantId,
        Guid holdingId,
        string roleCode,
        DesignationSubjectType subjectType,
        Guid? subjectId,
        string? externalFullName,
        string? externalRelationship,
        int rank,
        decimal? sharePercent,
        int clauseVersion,
        Guid recordedBy,
        DateTimeOffset now)
        => new()
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            HoldingId = holdingId,
            RoleCode = roleCode.Trim().ToUpperInvariant(),
            SubjectType = subjectType,
            SubjectId = subjectId,
            ExternalFullName = Blank(externalFullName),
            ExternalRelationship = Blank(externalRelationship),
            Rank = rank,
            SharePercent = sharePercent,
            Status = DesignationStatus.Active,
            ClauseVersion = clauseVersion,
            ValidFrom = now,
            RecordedBy = recordedBy,
            CreatedAt = now,
        };

    /// <summary>
    /// Flags the designation for a human decision because the person left the household
    /// (BNF-B-13). Never a delete, and never a silent removal of the share: the remaining
    /// designations of the rank no longer total 100%, and that is precisely what the agent has to
    /// be shown.
    /// </summary>
    internal void FlagForReview(string reason)
    {
        if (Status != DesignationStatus.Active)
            return;

        Status = DesignationStatus.ToReview;
        StatusReason = Blank(reason);
    }

    /// <summary>Closes this version. <paramref name="reason"/> is required by the caller, not here.</summary>
    internal void Close(string? reason, DateTimeOffset at)
    {
        if (ValidTo is not null)
            return;

        Status = DesignationStatus.Closed;
        StatusReason = Blank(reason) ?? StatusReason;
        ValidTo = at;
    }

    /// <summary>Blanks the name of a third party on anonymisation (BNF-B-19).</summary>
    internal void Anonymize()
    {
        ExternalFullName = null;
        ExternalRelationship = null;
    }

    private static string? Blank(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

/// <summary>What a designation points at (BNF-B-15).</summary>
public enum DesignationSubjectType
{
    /// <summary>A client of the tenant. <c>SubjectId</c> is their client id.</summary>
    Client,

    /// <summary>
    /// A household member who is not a client. <c>SubjectId</c> is the
    /// <c>ClientRelationship</c> row — which is also what makes "the member left the household"
    /// detectable without a name match.
    /// </summary>
    HouseholdMember,

    /// <summary>
    /// Somebody the tenant holds no record of. <c>SubjectId</c> is null and the name fields carry
    /// the identity: a death-benefit clause may name a friend, and refusing to record one until
    /// they become a client would make the clause unrecordable.
    /// </summary>
    ExternalPerson
}
