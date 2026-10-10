namespace Sankore.Modules.Leads.Domain;

using Sankore.Shared.Kernel;
using Sankore.Shared.Kernel.ValueObject;

/// <summary>
/// A spouse or a child of a lead's principal (BNF-B-03) — a child entity of the
/// <see cref="Lead"/> aggregate, reached only through it.
///
/// <para>
/// <b>Why it belongs to the aggregate and is not a table of its own.</b> The invariants are about
/// the SET: at most one active spouse, at most <c>MaxChildren</c> children, a child-age ceiling. A
/// standalone entity would leave every one of those to whichever handler happened to write, and the
/// unique index would be the only thing actually enforcing the first — too late and with an error
/// code nobody can act on.
/// </para>
///
/// <para>
/// <b>Removal is logical.</b> <see cref="RemovedAt"/>, never a delete: BNF-B-13 has a removed
/// member's active designations move to <c>ToReview</c> rather than vanish, and a household that
/// forgets a separation cannot answer who was covered when a claim was filed.
/// </para>
/// </summary>
public sealed class LeadHouseholdMember : ITenant
{
    public Guid Id { get; private set; }
    public Guid TenantId { get; private set; }
    public Guid LeadId { get; private set; }

    public HouseholdMemberType Type { get; private set; }

    /// <summary>Set for a spouse, null for a child — enforced here and by a CHECK constraint.</summary>
    public SpouseUnionType? SpouseUnionType { get; private set; }

    /// <summary>
    /// Whether this member lives at the principal's address (BNF-B-03).
    ///
    /// <para>
    /// A LINK, not a copy. When true the member's own postal fields stay empty and the address is
    /// resolved at READ time from the principal — see <see cref="ResolveIdentity"/>. Copying it
    /// would mean a principal who moves leaves a household at the old address, and nothing would
    /// say so.
    /// </para>
    /// </summary>
    public bool UsesPrincipalAddress { get; private set; }

    /// <summary>
    /// Identity as STORED. For a member that inherits the address this carries no postal fields —
    /// read it through <see cref="ResolveIdentity"/>, never directly.
    /// </summary>
    public PersonIdentity Identity { get; private set; } = default!;

    public PepStatus Pep { get; private set; } = PepStatus.NotExposed;

    /// <summary>
    /// When this member's PEP status was last declared, exposed or not (BNF-B-05). Null = never
    /// asked, which is NOT the same answer as "not exposed" — see <c>Lead.PepDeclaredAt</c> for
    /// why the status alone cannot carry the difference.
    ///
    /// <para>
    /// Also set when the principal's own <c>Self</c> exposure propagates onto this member: that
    /// propagation IS a declaration about the member, made by the rule rather than by the agent.
    /// </para>
    /// </summary>
    public DateTimeOffset? PepDeclaredAt { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset? RemovedAt { get; private set; }

    /// <summary>Why the member was removed. Kept: a separation and a data-entry error are not the same.</summary>
    public string? RemovalReason { get; private set; }

    public bool IsActive => RemovedAt is null;

    private LeadHouseholdMember() { } // EF Core

    internal static LeadHouseholdMember Create(
        Guid tenantId,
        Guid leadId,
        HouseholdMemberType type,
        SpouseUnionType? spouseUnionType,
        PersonIdentity identity,
        PepStatus? pep,
        DateTimeOffset? pepDeclaredAt,
        bool usesPrincipalAddress,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(identity);

        return new LeadHouseholdMember
        {
            Id                   = Guid.NewGuid(),
            TenantId             = tenantId,
            LeadId               = leadId,
            Type                 = type,
            SpouseUnionType      = spouseUnionType,
            Identity             = identity,
            Pep                  = pep ?? PepStatus.NotExposed,
            // Passed in rather than inferred from `pep`: the caller hands us a status either way
            // — an unexposed principal's propagated default is still a PepStatus — and only the
            // caller knows whether anybody actually declared it. See Lead.AddHouseholdMember.
            PepDeclaredAt        = pepDeclaredAt,
            UsesPrincipalAddress = usesPrincipalAddress,
            CreatedAt            = now,
        };
    }

    /// <summary>
    /// The identity to show, with the principal's address applied when this member inherits it.
    ///
    /// <para>
    /// Every read side goes through this. A projection that reads <see cref="Identity"/> directly
    /// shows a child with no address at all — which looks like missing data rather than like
    /// inheritance, and would be reported as incomplete by the completeness check.
    /// </para>
    /// </summary>
    public PersonIdentity ResolveIdentity(PersonIdentity? principalIdentity)
        => UsesPrincipalAddress && principalIdentity is not null
            ? Identity.WithAddressOf(principalIdentity)
            : Identity;

    /// <summary>
    /// Whether this member counts towards the lead's PEP exposure at <paramref name="asOf"/>.
    /// A removed member never does — they are no longer part of the household being assessed.
    /// </summary>
    internal bool IsPepExposedAt(DateOnly asOf, int postMandateMonths)
        => IsActive && Pep.IsExposedAt(asOf, postMandateMonths);

    internal Result UpdateIdentity(PersonIdentity identity, bool usesPrincipalAddress)
    {
        ArgumentNullException.ThrowIfNull(identity);

        if (!IsActive)
            return Result.Fail("HOUSEHOLD_MEMBER_REMOVED");

        Identity             = identity;
        UsesPrincipalAddress = usesPrincipalAddress;
        return Result.Ok();
    }

    internal Result UpdateUnionType(SpouseUnionType? unionType)
    {
        if (!IsActive)
            return Result.Fail("HOUSEHOLD_MEMBER_REMOVED");

        // The pairing is an invariant of the type, so it is re-checked on every write rather than
        // only at creation — a spouse whose union type was cleared would violate the CHECK
        // constraint at SaveChanges, with a database error instead of a result code.
        if (Type == HouseholdMemberType.Spouse && unionType is null)
            return Result.Fail("SPOUSE_UNION_TYPE_REQUIRED");
        if (Type == HouseholdMemberType.Child && unionType is not null)
            return Result.Fail("UNION_TYPE_FORBIDDEN_FOR_CHILD");

        SpouseUnionType = unionType;
        return Result.Ok();
    }

    internal Result SetPepStatus(PepStatus pep, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(pep);

        if (!IsActive)
            return Result.Fail("HOUSEHOLD_MEMBER_REMOVED");

        Pep           = pep;
        PepDeclaredAt = now;
        return Result.Ok();
    }

    internal Result Remove(string reason, DateTimeOffset now)
    {
        if (!IsActive)
            return Result.Fail("HOUSEHOLD_MEMBER_ALREADY_REMOVED");
        if (string.IsNullOrWhiteSpace(reason))
            return Result.Fail("HOUSEHOLD_REMOVAL_REASON_REQUIRED");

        RemovedAt     = now;
        RemovalReason = reason.Trim();
        return Result.Ok();
    }

    /// <summary>
    /// Blanks the member's identity in place (BNF-B-19). The row, its type and its links survive:
    /// an anonymised household must still show that there WAS a spouse.
    /// </summary>
    internal void Anonymize()
    {
        Identity      = Identity.Anonymized();
        Pep           = PepStatus.NotExposed;
        RemovalReason = null;
    }
}
