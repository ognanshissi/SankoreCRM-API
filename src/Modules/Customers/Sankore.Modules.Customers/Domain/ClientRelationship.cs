namespace Sankore.Modules.Customers.Domain;

using Sankore.Shared.Kernel;
using Sankore.Shared.Kernel.ValueObject;

/// <summary>
/// A link between a client and either another client of the tenant (<see cref="ToClient"/>)
/// or a person who is not a client (<see cref="ToExternal"/>).
/// Client-to-client links are created in pairs: each row points at its mirror through
/// <see cref="ReciprocalRelationshipId"/>, so closing one closes the story on both sides.
///
/// <para>
/// <b>This table is also the client's HOUSEHOLD</b> (BNF-B-13). There is deliberately no second
/// <c>CustomerHouseholdMember</c> table: this one already models a spouse and a child, internally
/// (<see cref="RelatedClientId"/>) and externally (<see cref="ToExternal"/>), with temporal
/// validity and reciprocity — and the logical removal the story asks for is already
/// <see cref="ValidTo"/> plus <see cref="CloseReason"/>. A parallel table would give two places to
/// record a spouse, which would diverge; eliminating exactly that divergence is the point of the
/// whole evolution.
/// </para>
///
/// <para>
/// The household is the filtered view <see cref="IsHouseholdMember"/>:
/// <c>Type ∈ {Spouse, Child, Dependent}</c> and <c>ValidTo IS NULL</c>. The "at most one active
/// spouse" invariant is a partial unique index, not a read — see
/// <c>ClientRelationshipConfiguration.OneSpouseIndex</c>.
/// </para>
/// </summary>
public sealed class ClientRelationship
{
    public Guid Id { get; private set; }
    public Guid TenantId { get; private set; }
    public Guid ClientId { get; private set; }
    public RelationshipType Type { get; private set; }

    public Guid? RelatedClientId { get; private set; }
    public string? ExternalFullName { get; private set; }
    public string? ExternalPhoneEncrypted { get; private set; }
    public string? ExternalPhoneBlindIndex { get; private set; }
    public DateOnly? ExternalDateOfBirth { get; private set; }
    public string? EncryptedExternalDocumentNumber { get; private set; }

    // ── Household (BNF-B-13) ─────────────────────────────────────────────────

    /// <summary>
    /// Set for a <c>Spouse</c>, null for everyone else — enforced here and by a CHECK constraint.
    /// </summary>
    public SpouseUnionType? SpouseUnionType { get; private set; }

    /// <summary>
    /// Whether this member lives at the principal client's address (BNF-B-13).
    ///
    /// <para>
    /// A LINK, not a copy — the same rule as <c>LeadHouseholdMember.UsesPrincipalAddress</c>. When
    /// true the member's own postal fields stay empty and the address is resolved at READ time from
    /// the client. Copying it would mean a client who moves leaves a household at the old address
    /// with nothing saying so.
    /// </para>
    /// </summary>
    public bool UsesPrincipalAddress { get; private set; }

    /// <summary>
    /// This person's own PEP status (BNF-B-13). Never null — <see cref="PepStatus.NotExposed"/>
    /// until somebody declares otherwise.
    /// </summary>
    public PepStatus Pep { get; private set; } = PepStatus.NotExposed;

    /// <summary>
    /// When the PEP status was last DECLARED, exposed or not. Null = never asked, which is not the
    /// same answer as "not exposed" — see <c>Lead.PepDeclaredAt</c> for why the status alone cannot
    /// carry the difference.
    /// </summary>
    public DateTimeOffset? PepDeclaredAt { get; private set; }

    /// <summary>
    /// The <c>LeadHouseholdMember</c> this row was created from, when the household arrived with a
    /// conversion (BNF-B-13). An OPAQUE reference into the leads schema with no foreign key: it
    /// exists so a reviewer can trace a household back to the conversation that collected it, and
    /// every reader must cope with it no longer resolving.
    /// </summary>
    public Guid? SourceLeadMemberId { get; private set; }

    // ── A non-client member's own identity ───────────────────────────────────
    //
    // Flat columns plus a projection, NOT an owned PersonIdentity — the same decision as
    // Lead.PrincipalIdentity, and for the same reason: ExternalFullName, ExternalDateOfBirth and
    // EncryptedExternalDocumentNumber are columns the reveal endpoint, the merge executor and the
    // importers already read, and converting them to an owned type would run through all of that
    // for a gain of pure form. These complete the set so the projection can be a WHOLE person,
    // which is what a member inheriting the client's address needs.

    public string? ExternalFirstName { get; private set; }
    public string? ExternalLastName { get; private set; }
    public string? ExternalUsageName { get; private set; }
    public PersonGender ExternalGender { get; private set; }

    /// <summary>SENSITIVE — encrypted at rest. Never comparable in SQL.</summary>
    public string? ExternalAddressEncrypted { get; private set; }

    public string? ExternalPostalCode { get; private set; }
    public string? ExternalCity { get; private set; }

    /// <summary>ISO 3166-1 alpha-2, upper-cased.</summary>
    public string? ExternalCountryCode { get; private set; }

    public Guid? ReciprocalRelationshipId { get; private set; }
    public DateTimeOffset ValidFrom { get; private set; }
    public DateTimeOffset? ValidTo { get; private set; }
    public string? CloseReason { get; private set; }
    public Guid CreatedBy { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }

    public bool IsActive => ValidTo is null;

    /// <summary>
    /// Whether this link is part of the client's household (BNF-B-13) — the filtered view that
    /// replaces a second table. A guarantor, a proxy, a parent or a sibling is a relationship and
    /// not a household member: the one-spouse invariant and the child-age ceiling would be
    /// ambiguous over them.
    /// </summary>
    public bool IsHouseholdMember
        => Type is RelationshipType.Spouse or RelationshipType.Child or RelationshipType.Dependent;

    /// <summary>
    /// Whether the member is a client of the tenant in their own right. Their identity then lives
    /// on that client record and NOT here — <see cref="ResolveIdentity"/> returns null, and a read
    /// side has to join.
    /// </summary>
    public bool IsClientLinked => RelatedClientId is not null;

    private ClientRelationship() { } // EF Core

    public static ClientRelationship ToClient(
        Guid tenantId,
        Guid clientId,
        RelationshipType type,
        Guid relatedClientId,
        DateTimeOffset validFrom,
        Guid createdBy)
    {
        if (relatedClientId == Guid.Empty)
            throw new DomainException("Related client is required.", "ClientRelationship.RelatedClient.Required");
        if (relatedClientId == clientId)
            throw new DomainException("A client cannot be related to itself.", "ClientRelationship.Self.Forbidden");

        return new ClientRelationship
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            ClientId = clientId,
            Type = type,
            RelatedClientId = relatedClientId,
            ValidFrom = validFrom,
            CreatedBy = createdBy,
            CreatedAt = DateTimeOffset.UtcNow,
        };
    }

    public static ClientRelationship ToExternal(
        Guid tenantId,
        Guid clientId,
        RelationshipType type,
        string externalFullName,
        string? externalPhoneEncrypted,
        string? externalPhoneBlindIndex,
        DateOnly? externalDateOfBirth,
        string? encryptedExternalDocumentNumber,
        DateTimeOffset validFrom,
        Guid createdBy)
    {
        if (string.IsNullOrWhiteSpace(externalFullName))
            throw new DomainException("Related person name is required.", "ClientRelationship.ExternalFullName.Required");

        return new ClientRelationship
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            ClientId = clientId,
            Type = type,
            ExternalFullName = externalFullName.Trim(),
            ExternalPhoneEncrypted = externalPhoneEncrypted,
            ExternalPhoneBlindIndex = externalPhoneBlindIndex,
            ExternalDateOfBirth = externalDateOfBirth,
            EncryptedExternalDocumentNumber = encryptedExternalDocumentNumber,
            ValidFrom = validFrom,
            CreatedBy = createdBy,
            CreatedAt = DateTimeOffset.UtcNow,
        };
    }

    // ── Household members (BNF-B-13) ─────────────────────────────────────────

    /// <summary>
    /// Records a household member who is NOT a client of the tenant — the common case for a child.
    /// </summary>
    /// <param name="identity">
    /// The member's own identity. Its postal fields are DROPPED when
    /// <paramref name="usesPrincipalAddress"/> is true: cleared on write and not merely ignored on
    /// read, because a stale address left in the row is one a later "stop inheriting" silently
    /// resurrects, years out of date.
    /// </param>
    /// <param name="encryptAddress">
    /// The module's field encryptor, applied here rather than by a value converter: this column
    /// sits next to three other hand-encrypted ones on the same entity
    /// (<see cref="ExternalPhoneEncrypted"/>, <see cref="EncryptedExternalDocumentNumber"/>), and
    /// mixing the two styles on one table is how a reader loses track of which is which.
    /// </param>
    /// <param name="sourceLeadMemberId">
    /// Set when the member arrives with a lead conversion; null when an agent records them
    /// directly.
    /// </param>
    public static ClientRelationship HouseholdMember(
        Guid tenantId,
        Guid clientId,
        RelationshipType type,
        PersonIdentity identity,
        SpouseUnionType? spouseUnionType,
        PepStatus? pep,
        bool usesPrincipalAddress,
        Func<string, string> encryptAddress,
        DateTimeOffset validFrom,
        Guid createdBy,
        Guid? relatedClientId = null,
        Guid? sourceLeadMemberId = null,
        string? externalPhoneEncrypted = null,
        string? externalPhoneBlindIndex = null,
        string? encryptedExternalDocumentNumber = null)
    {
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentNullException.ThrowIfNull(encryptAddress);

        if (type is not (RelationshipType.Spouse or RelationshipType.Child or RelationshipType.Dependent))
            throw new DomainException(
                "Only a spouse, a child or a dependent is a household member.",
                "ClientRelationship.Household.TypeForbidden");

        // Both halves, because each on its own is a different kind of wrong record: a spouse with
        // no union type cannot be reported to a regulator, and a child carrying one is a data-entry
        // error nobody would notice.
        if (type == RelationshipType.Spouse && spouseUnionType is null)
            throw new DomainException(
                "A spouse needs a union type.", "ClientRelationship.Household.UnionTypeRequired");

        if (type != RelationshipType.Spouse && spouseUnionType is not null)
            throw new DomainException(
                "Only a spouse carries a union type.",
                "ClientRelationship.Household.UnionTypeForbidden");

        var stored = usesPrincipalAddress ? identity.WithoutPostalAddress() : identity;

        return new ClientRelationship
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            ClientId = clientId,
            Type = type,
            RelatedClientId = relatedClientId,
            SourceLeadMemberId = sourceLeadMemberId,
            SpouseUnionType = spouseUnionType,
            UsesPrincipalAddress = usesPrincipalAddress,
            Pep = pep ?? PepStatus.NotExposed,
            // Only when one was SUPPLIED — a member recorded without a PEP status has not been
            // asked, and stamping `validFrom` would record an answer nobody gave.
            PepDeclaredAt = pep is null ? null : validFrom,
            ExternalPhoneEncrypted = externalPhoneEncrypted,
            ExternalPhoneBlindIndex = externalPhoneBlindIndex,
            EncryptedExternalDocumentNumber = encryptedExternalDocumentNumber,
            ValidFrom = validFrom,
            CreatedBy = createdBy,
            CreatedAt = DateTimeOffset.UtcNow,
        }.ApplyIdentity(stored, encryptAddress);
    }

    /// <summary>
    /// Replaces the member's own identity. Returns
    /// <c>CLIENT_RELATIONSHIP_CLOSED</c> when the link is no longer active, or
    /// <c>HOUSEHOLD_MEMBER_IS_A_CLIENT</c> when the identity belongs to a client record instead.
    /// </summary>
    public Result UpdateHouseholdIdentity(
        PersonIdentity identity, bool usesPrincipalAddress, Func<string, string> encryptAddress)
    {
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentNullException.ThrowIfNull(encryptAddress);

        if (!IsActive)
            return Result.Fail(CustomerErrors.RelationshipClosed);

        // A member who is a client in their own right has their identity on that record. Writing
        // it here would create a second, diverging copy — the thing this table exists to avoid.
        if (IsClientLinked)
            return Result.Fail(CustomerErrors.HouseholdMemberIsAClient);

        ApplyIdentity(usesPrincipalAddress ? identity.WithoutPostalAddress() : identity, encryptAddress);
        UsesPrincipalAddress = usesPrincipalAddress;
        return Result.Ok();
    }

    /// <summary>Declares this member's PEP status (BNF-B-13). An explicit negative is a declaration.</summary>
    public Result SetPepStatus(PepStatus pep, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(pep);

        if (!IsActive)
            return Result.Fail(CustomerErrors.RelationshipClosed);

        Pep = pep;
        PepDeclaredAt = now;
        return Result.Ok();
    }

    /// <summary>Changes a spouse's union type. Refused on anyone else.</summary>
    public Result UpdateUnionType(SpouseUnionType unionType)
    {
        if (!IsActive)
            return Result.Fail(CustomerErrors.RelationshipClosed);

        if (Type != RelationshipType.Spouse)
            return Result.Fail(CustomerErrors.UnionTypeForbidden);

        SpouseUnionType = unionType;
        return Result.Ok();
    }

    /// <summary>
    /// The member's identity as a value object, with the client's address applied when they
    /// inherit it. Null when either name is missing, and null when the member is a client in their
    /// own right — the identity is then on that record and the caller has to join.
    /// </summary>
    /// <remarks>
    /// Every read side goes through this. A projection reading the columns directly shows a child
    /// with no address at all, which looks like missing data rather than like inheritance — and
    /// the completeness check would report it as a gap.
    /// </remarks>
    public PersonIdentity? ResolveIdentity(
        PersonIdentity? principalIdentity, Func<string, string> decryptAddress)
    {
        ArgumentNullException.ThrowIfNull(decryptAddress);

        if (IsClientLinked
            || string.IsNullOrWhiteSpace(ExternalFirstName)
            || string.IsNullOrWhiteSpace(ExternalLastName))
            return null;

        var identity = PersonIdentity.Create(
            lastName:    ExternalLastName,
            firstName:   ExternalFirstName,
            usageName:   ExternalUsageName,
            birthDate:   ExternalDateOfBirth,
            gender:      ExternalGender,
            addressLine: ExternalAddressEncrypted is null ? null : decryptAddress(ExternalAddressEncrypted),
            postalCode:  ExternalPostalCode,
            city:        ExternalCity,
            countryCode: ExternalCountryCode);

        return UsesPrincipalAddress && principalIdentity is not null
            ? identity.WithAddressOf(principalIdentity)
            : identity;
    }

    /// <summary>Whether this member is politically exposed at <paramref name="asOf"/>.</summary>
    internal bool IsPepExposedAt(DateOnly asOf, int postMandateMonths)
        => Pep.IsExposedAt(asOf, postMandateMonths);

    /// <summary>
    /// Writes the identity onto the flat columns. <see cref="ExternalFullName"/> is kept in step
    /// because it is what every pre-existing projection, the merge executor and the duplicate
    /// scorer read — leaving it stale would make a renamed spouse show their old name everywhere
    /// except the household screen.
    /// </summary>
    private ClientRelationship ApplyIdentity(
        PersonIdentity identity, Func<string, string> encryptAddress)
    {
        ExternalFirstName   = identity.FirstName;
        ExternalLastName    = identity.LastName;
        ExternalUsageName   = identity.UsageName;
        ExternalGender      = identity.Gender;
        ExternalDateOfBirth = identity.BirthDate;
        ExternalPostalCode  = identity.PostalCode;
        ExternalCity        = identity.City;
        ExternalCountryCode = identity.CountryCode;
        ExternalFullName    = identity.FullName;

        ExternalAddressEncrypted = identity.AddressLine is null
            ? null
            : encryptAddress(identity.AddressLine);

        return this;
    }

    internal void LinkReciprocal(Guid id) => ReciprocalRelationshipId = id;

    public void Close(string? reason, DateTimeOffset at)
    {
        if (ValidTo is not null)
            return;

        ValidTo = at;
        CloseReason = string.IsNullOrWhiteSpace(reason) ? null : reason.Trim();
    }

    /// <summary>Re-parents the relationship onto the surviving client of a merge.</summary>
    internal void ReassignTo(Guid clientId) => ClientId = clientId;
}
