using Sankore.Modules.Kyc.PublicApi;

namespace Sankore.Modules.Customers.Domain;

using Sankore.Modules.Customers.Domain.Matching;
using Sankore.Modules.Customers.Domain.Events;
using Sankore.Shared.Kernel;
using Sankore.Shared.Kernel.ValueObject;

/// <summary>
/// Aggregate root of module M01: a client, individual or legal entity.
/// <para>
/// The aggregate never sees clear personal data for the protected fields: the feature slice
/// encrypts (<c>IFieldEncryptor</c>) and blind-indexes (<c>IBlindIndexer</c>) before calling in,
/// so the domain only ever stores already-encrypted strings and their search indexes.
/// </para>
/// <para>
/// Lifecycle: <c>PendingKyc</c> → <c>Active</c> ⇄ <c>Suspended</c>, <c>PendingKyc</c> → <c>KycRejected</c>,
/// and the two terminal states <c>Archived</c> / <c>Merged</c>, which make the client read-only.
/// Every transition appends a <see cref="ClientStatusHistory"/> row.
/// </para>
/// </summary>
public sealed class Client : AggregateRoot
{
    private readonly List<ClientContactPoint> _contactPoints = [];
    private readonly List<ClientStatusHistory> _statusHistory = [];

    // ── Identity ────────────────────────────────────────────────────────────
    public Guid Id { get; private set; }
    public string ClientNumber { get; private set; } = default!;
    public ClientType Type { get; private set; }
    public ClientStatus Status { get; private set; }

    // ── Ownership ───────────────────────────────────────────────────────────
    public Guid AgencyId { get; private set; }
    public string AgencyCode { get; private set; } = default!;
    public Guid? AdvisorUserId { get; private set; }

    /// <summary>Denormalized label shown everywhere. Recomputed on every identity change.</summary>
    public string DisplayName { get; private set; } = default!;

    /// <summary>
    /// Upper-cased, accent-free projection of the name, surname first, maintained by
    /// <see cref="SetDisplayName"/>. Backs the indexed prefix search of US-M01-BE-12 —
    /// always normalize the search term through <see cref="SearchKeyBuilder.NormalizeTerm"/>.
    /// </summary>
    public string SearchKey { get; private set; } = default!;

    // ── Individual identity ─────────────────────────────────────────────────
    public string? FirstName { get; private set; }
    public string? LastName { get; private set; }
    public string? MaidenName { get; private set; }
    public Gender? Gender { get; private set; }
    public string? EncryptedDateOfBirth { get; private set; }
    public string? DateOfBirthBlindIndex { get; private set; }
    public string? BirthPlace { get; private set; }
    public string? Nationality { get; private set; }
    public MaritalStatus? MaritalStatus { get; private set; }
    public string? FatherName { get; private set; }
    public string? MotherName { get; private set; }

    // ── Socio-economic ──────────────────────────────────────────────────────
    public string? Profession { get; private set; }
    public string? Employer { get; private set; }
    public string? EncryptedDeclaredIncome { get; private set; }
    public string? DeclaredIncomeCurrency { get; private set; }
    public string PreferredLanguage { get; private set; } = default!;
    public int DependentsCount { get; private set; }

    /// <summary>
    /// Values of the tenant's custom client fields, keyed by field key (dynamic forms, lot 1) —
    /// checked and merged by <c>CustomFieldValues.Apply</c> before they reach here. In clear,
    /// like the names: a custom field marked sensitive is masked by the screens and cleared by
    /// anonymisation, not encrypted.
    /// </summary>
    public string? CustomFieldsJson { get; private set; }

    // ── AML / LBC-FT profile ────────────────────────────────────────────────
    //
    // The CIMA LBC/FT regulation (article 13.1 of the 2021 text, carried into the 2025 one) is
    // explicit that a profession alone is not enough: « les mentions vagues (commerçant, homme
    // d'affaires) ne suffisent pas ; il faut se renseigner sur le secteur d'activité du client ».
    // Profession above is what the client calls themselves; these two are what the institution
    // established.

    /// <summary>
    /// The client's sector of activity, as a CODE.
    ///
    /// <para>
    /// Deliberately the SAME referential the core banking mapping already expects —
    /// <c>MappingDomain.Sector</c> in M-Integration — rather than a second list of this module's
    /// own. A client whose sector cannot be sent to the CBS is a client whose sector was recorded
    /// for nobody.
    /// </para>
    /// </summary>
    public string? ActivitySectorCode { get; private set; }

    /// <summary>
    /// What the institution actually established about the activity, in words. In clear: it is a
    /// description of a trade, not an identifier of a person, and a compliance officer reads it.
    /// </summary>
    public string? ActivitySectorDetail { get; private set; }

    /// <summary>
    /// Where the client's assets and funds come from, as a code from a closed list.
    ///
    /// <para>
    /// Required of a POLITICALLY EXPOSED person — « établir l'origine du patrimoine et des
    /// fonds » — and useful of anybody. It is not gated on the PEP flag here: the field records a
    /// fact about the client, and the gate that makes it mandatory belongs to the KYC approval
    /// path, where the vigilance level is known.
    /// </para>
    /// </summary>
    public string? FundsOriginCode { get; private set; }

    /// <summary>
    /// The narrative behind <see cref="FundsOriginCode"/>, ENCRYPTED: it describes a named
    /// person's assets, which is the most sensitive sentence in a client file after their health.
    /// No blind index — nothing searches for a client by the origin of their wealth, and an index
    /// would make "who inherited money" a query.
    /// </summary>
    public string? EncryptedFundsOriginDetail { get; private set; }

    // ── Identity document ───────────────────────────────────────────────────
    public IdentityDocumentType? IdentityDocumentType { get; private set; }
    public string? EncryptedIdentityDocumentNumber { get; private set; }
    public string? IdentityDocumentNumberBlindIndex { get; private set; }
    public DateOnly? IdentityDocumentIssuedOn { get; private set; }
    public DateOnly? IdentityDocumentExpiresOn { get; private set; }

    // ── Legal entity ────────────────────────────────────────────────────────
    public string? LegalName { get; private set; }
    public string? LegalFormCode { get; private set; }
    public string? EncryptedRegistrationNumber { get; private set; }
    public string? RegistrationNumberBlindIndex { get; private set; }
    public string? EncryptedTaxIdNumber { get; private set; }
    public DateOnly? IncorporationDate { get; private set; }

    /// <summary>
    /// The company's stated object (<i>objet social</i>), required of a legal person by article
    /// 13.2. In clear — it is public registry data, printed on the statutes.
    /// </summary>
    public string? CorporatePurpose { get; private set; }

    /// <summary>
    /// When the registry extract on file was ISSUED, not when the company was registered.
    ///
    /// <para>
    /// Article 13.2 asks for « un extrait du registre du commerce et du crédit mobilier de moins
    /// de trois mois ». That is a property of the DOCUMENT, so it cannot be derived from
    /// <see cref="IncorporationDate"/> — and the freshness rule is enforced where the document is
    /// REQUIRED (creating a legal client, validating its KYC) rather than as an invariant here: a
    /// row recorded two years ago must not become invalid tonight.
    /// </para>
    /// </summary>
    public DateOnly? RegistrationExtractIssuedOn { get; private set; }

    // ── Duplicate detection ─────────────────────────────────────────────────
    public string? PhoneticKeyPrimary { get; private set; }
    public string? PhoneticKeySecondary { get; private set; }

    // ── KYC & risk ──────────────────────────────────────────────────────────
    public KycStatus KycStatus { get; private set; }
    public string? KycRejectionReason { get; private set; }
    public RiskLevel RiskLevel { get; private set; }
    public DateTimeOffset? KycUpdatedAt { get; private set; }

    // ── Segmentation & loyalty ──────────────────────────────────────────────
    public string? SegmentCode { get; private set; }
    public DateTimeOffset? SegmentAssignedAt { get; private set; }
    public int? LoyaltyScore { get; private set; }
    public bool LoyaltyScoreProvisional { get; private set; }
    public DateTimeOffset? LoyaltyScoreAt { get; private set; }

    // ── Provenance & end of life ────────────────────────────────────────────
    public Guid? SourceLeadId { get; private set; }
    public Guid? MergedIntoId { get; private set; }
    public DateTimeOffset? ArchivedAt { get; private set; }
    public string? ArchiveReason { get; private set; }
    public bool IsAnonymized { get; private set; }

    // ── Audit & concurrency ─────────────────────────────────────────────────
    public Guid CreatedBy { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public Guid? UpdatedBy { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }

    /// <summary>PostgreSQL <c>xmin</c>, mapped as the optimistic concurrency token.</summary>
    public uint Version { get; private set; }

    // ── PEP (BNF-B-13) ──────────────────────────────────────────────────────

    /// <summary>
    /// The client's OWN political exposure. Never null — <see cref="PepStatus.NotExposed"/> until
    /// somebody declares otherwise, and <see cref="PepDeclaredAt"/> is what separates that default
    /// from an answer.
    ///
    /// <para>
    /// Written at conversion from the lead's <c>PrincipalPep</c> and by the household endpoints
    /// afterwards. It is NOT derived from <see cref="RiskLevel"/> or from the KYC file: the
    /// vigilance level M02 computes is a CONSEQUENCE of this, so reading it back would be circular.
    /// </para>
    /// </summary>
    public PepStatus PrincipalPep { get; private set; } = PepStatus.NotExposed;

    /// <summary>Null = never asked. See <c>Lead.PepDeclaredAt</c>.</summary>
    public DateTimeOffset? PepDeclaredAt { get; private set; }

    /// <summary>
    /// Whether anybody in this client's household is politically exposed (BNF-B-13) — the flag a
    /// KYC review and a designation screen read.
    ///
    /// <para>
    /// STORED and not computed, for the same reasons as <c>Lead.HasPepExposure</c>: a screen
    /// filters on it in SQL, and a RISE has to raise an event at the moment it happens, which a
    /// computed property cannot notice. <see cref="RecalculateHasPepExposure"/> is the only writer.
    /// </para>
    /// </summary>
    public bool HasPepExposure { get; private set; }

    // ── Children ────────────────────────────────────────────────────────────
    public IReadOnlyCollection<ClientContactPoint> ContactPoints => _contactPoints.AsReadOnly();
    public IReadOnlyCollection<ClientStatusHistory> StatusHistory => _statusHistory.AsReadOnly();

    /// <summary>Archived and merged clients accept no mutation at all.</summary>
    public bool IsReadOnly => Status is ClientStatus.Archived or ClientStatus.Merged;

    private Client() { } // EF Core

    // ── Factories ───────────────────────────────────────────────────────────

    public static Client CreateIndividual(
        Guid tenantId,
        string clientNumber,
        Guid agencyId,
        string agencyCode,
        Guid? advisorUserId,
        string firstName,
        string lastName,
        string? maidenName,
        Gender gender,
        string? encryptedDateOfBirth,
        string? dateOfBirthBlindIndex,
        string? birthPlace,
        string? nationality,
        MaritalStatus? maritalStatus,
        string? fatherName,
        string? motherName,
        string? profession,
        string? employer,
        string? encryptedDeclaredIncome,
        string? declaredIncomeCurrency,
        string preferredLanguage,
        IdentityDocumentType? docType,
        string? encryptedDocNumber,
        string? docNumberBlindIndex,
        DateOnly? docIssuedOn,
        DateOnly? docExpiresOn,
        Guid createdBy,
        Guid? sourceLeadId = null,
        Guid? id = null)
    {
        if (string.IsNullOrWhiteSpace(clientNumber))
            throw new DomainException("Client number is required.", "Client.ClientNumber.Required");
        if (string.IsNullOrWhiteSpace(firstName))
            throw new DomainException("First name is required.", "Client.FirstName.Required");
        if (string.IsNullOrWhiteSpace(lastName))
            throw new DomainException("Last name is required.", "Client.LastName.Required");
        if (agencyId == Guid.Empty)
            throw new DomainException("An agency is required.", "Client.Agency.Required");
        if (string.IsNullOrWhiteSpace(agencyCode))
            throw new DomainException("Agency code is required.", "Client.AgencyCode.Required");

        var now = DateTimeOffset.UtcNow;

        var client = new Client
        {
            Id = id ?? Guid.NewGuid(),
            TenantId = tenantId,
            ClientNumber = clientNumber.Trim(),
            Type = ClientType.Individual,
            Status = ClientStatus.PendingKyc,
            AgencyId = agencyId,
            AgencyCode = agencyCode.Trim(),
            AdvisorUserId = advisorUserId,
            FirstName = firstName.Trim(),
            LastName = lastName.Trim(),
            MaidenName = Clean(maidenName),
            Gender = gender,
            EncryptedDateOfBirth = encryptedDateOfBirth,
            DateOfBirthBlindIndex = dateOfBirthBlindIndex,
            BirthPlace = Clean(birthPlace),
            Nationality = Clean(nationality),
            MaritalStatus = maritalStatus,
            FatherName = Clean(fatherName),
            MotherName = Clean(motherName),
            Profession = Clean(profession),
            Employer = Clean(employer),
            EncryptedDeclaredIncome = encryptedDeclaredIncome,
            DeclaredIncomeCurrency = Clean(declaredIncomeCurrency),
            PreferredLanguage = DefaultLanguage(preferredLanguage),
            DependentsCount = 0,
            IdentityDocumentType = docType,
            EncryptedIdentityDocumentNumber = encryptedDocNumber,
            IdentityDocumentNumberBlindIndex = docNumberBlindIndex,
            IdentityDocumentIssuedOn = docIssuedOn,
            IdentityDocumentExpiresOn = docExpiresOn,
            KycStatus = KycStatus.NotStarted,
            RiskLevel = RiskLevel.Unknown,
            LoyaltyScoreProvisional = false,
            IsAnonymized = false,
            SourceLeadId = sourceLeadId,
            CreatedBy = createdBy,
            CreatedAt = now,
            UpdatedAt = now,
        };

        client.SetDisplayName(
            BuildPersonDisplayName(client.FirstName, client.LastName),
            SearchKeyBuilder.ForPerson(client.FirstName, client.LastName));
        client._statusHistory.Add(ClientStatusHistory.Create(
            tenantId, client.Id, null, ClientStatus.PendingKyc, null, createdBy, now));
        client.RaiseDomainEvent(new ClientCreatedDomainEvent(client.Id));

        return client;
    }

    public static Client CreateLegal(
        Guid tenantId,
        string clientNumber,
        Guid agencyId,
        string agencyCode,
        Guid? advisorUserId,
        string legalName,
        string legalFormCode,
        string? encryptedRegistrationNumber,
        string? registrationNumberBlindIndex,
        string? encryptedTaxIdNumber,
        DateOnly? incorporationDate,
        string preferredLanguage,
        Guid createdBy,
        Guid? sourceLeadId = null,
        Guid? id = null)
    {
        if (string.IsNullOrWhiteSpace(clientNumber))
            throw new DomainException("Client number is required.", "Client.ClientNumber.Required");
        if (string.IsNullOrWhiteSpace(legalName))
            throw new DomainException("Legal name is required.", "Client.LegalName.Required");
        if (string.IsNullOrWhiteSpace(legalFormCode))
            throw new DomainException("Legal form is required.", "Client.LegalForm.Required");
        if (agencyId == Guid.Empty)
            throw new DomainException("An agency is required.", "Client.Agency.Required");
        if (string.IsNullOrWhiteSpace(agencyCode))
            throw new DomainException("Agency code is required.", "Client.AgencyCode.Required");

        var now = DateTimeOffset.UtcNow;

        var client = new Client
        {
            Id = id ?? Guid.NewGuid(),
            TenantId = tenantId,
            ClientNumber = clientNumber.Trim(),
            Type = ClientType.Legal,
            Status = ClientStatus.PendingKyc,
            AgencyId = agencyId,
            AgencyCode = agencyCode.Trim(),
            AdvisorUserId = advisorUserId,
            LegalName = legalName.Trim(),
            LegalFormCode = legalFormCode.Trim(),
            EncryptedRegistrationNumber = encryptedRegistrationNumber,
            RegistrationNumberBlindIndex = registrationNumberBlindIndex,
            EncryptedTaxIdNumber = encryptedTaxIdNumber,
            IncorporationDate = incorporationDate,
            PreferredLanguage = DefaultLanguage(preferredLanguage),
            DependentsCount = 0,
            KycStatus = KycStatus.NotStarted,
            RiskLevel = RiskLevel.Unknown,
            LoyaltyScoreProvisional = false,
            IsAnonymized = false,
            SourceLeadId = sourceLeadId,
            CreatedBy = createdBy,
            CreatedAt = now,
            UpdatedAt = now,
        };

        client.SetDisplayName(client.LegalName!, SearchKeyBuilder.ForLegalEntity(client.LegalName));
        client._statusHistory.Add(ClientStatusHistory.Create(
            tenantId, client.Id, null, ClientStatus.PendingKyc, null, createdBy, now));
        client.RaiseDomainEvent(new ClientCreatedDomainEvent(client.Id));

        return client;
    }

    // ── Duplicate detection keys ────────────────────────────────────────────

    /// <summary>Set by the feature slice from <c>IPhoneticKeyCalculator</c> — never computed here.</summary>
    public void SetPhoneticKeys(string? primary, string? secondary)
    {
        PhoneticKeyPrimary = primary;
        PhoneticKeySecondary = secondary;
    }

    // ── Updates ─────────────────────────────────────────────────────────────

    public Result UpdateNonSensitive(
        string? profession,
        string? employer,
        MaritalStatus? maritalStatus,
        string? encryptedDeclaredIncome,
        string? declaredIncomeCurrency,
        string? preferredLanguage,
        Guid actor)
    {
        if (IsReadOnly)
            return Result.Fail(CustomerErrors.ClientReadOnly);

        if (profession is not null) Profession = Clean(profession);
        if (employer is not null) Employer = Clean(employer);
        if (maritalStatus is not null) MaritalStatus = maritalStatus;
        if (encryptedDeclaredIncome is not null) EncryptedDeclaredIncome = encryptedDeclaredIncome;
        if (declaredIncomeCurrency is not null) DeclaredIncomeCurrency = Clean(declaredIncomeCurrency);
        if (!string.IsNullOrWhiteSpace(preferredLanguage)) PreferredLanguage = DefaultLanguage(preferredLanguage);

        Touch(actor);
        return Result.Ok();
    }

    /// <summary>Stores custom-field values already validated against the tenant's client form.</summary>
    public Result SetCustomFields(string? json, Guid actor)
    {
        if (IsReadOnly)
            return Result.Fail(CustomerErrors.ClientReadOnly);

        if (string.Equals(CustomFieldsJson, json, StringComparison.Ordinal))
            return Result.Ok();

        CustomFieldsJson = json;
        Touch(actor);
        return Result.Ok();
    }

    /// <summary>
    /// Records what the institution established for its LBC/FT file: the sector of activity, the
    /// origin of the funds, and — for a legal person — the stated object and the issue date of the
    /// registry extract on file.
    ///
    /// <para>
    /// Its own method rather than widening <see cref="UpdateNonSensitive"/>, for two reasons. It
    /// names the purpose, which is what an auditor reading this aggregate is looking for; and the
    /// funds-origin narrative is ENCRYPTED, so it does not belong among fields a caller passes in
    /// clear.
    /// </para>
    ///
    /// <para>
    /// <b>Null leaves alone, blank clears</b> — the convention
    /// <c>Lead.EnrichPrincipalIdentity</c> established. A compliance file is completed over
    /// several conversations from a screen that posts only what it collected, so a null that
    /// cleared would make each save erase the last.
    /// </para>
    /// </summary>
    public Result UpdateAmlProfile(
        string? activitySectorCode,
        string? activitySectorDetail,
        string? fundsOriginCode,
        string? encryptedFundsOriginDetail,
        string? corporatePurpose,
        DateOnly? registrationExtractIssuedOn,
        Guid actor)
    {
        if (IsReadOnly)
            return Result.Fail(CustomerErrors.ClientReadOnly);

        if (activitySectorCode is not null)
            ActivitySectorCode = Clean(activitySectorCode)?.ToUpperInvariant();

        if (activitySectorDetail is not null) ActivitySectorDetail = Clean(activitySectorDetail);
        if (fundsOriginCode is not null) FundsOriginCode = Clean(fundsOriginCode)?.ToUpperInvariant();

        if (encryptedFundsOriginDetail is not null)
        {
            // Blank clears, and clearing means clearing the CIPHERTEXT — an empty string
            // encrypted is still a payload, and a file "with no origin recorded" must read as
            // null rather than as a decryptable blank.
            EncryptedFundsOriginDetail = encryptedFundsOriginDetail.Length == 0
                ? null
                : encryptedFundsOriginDetail;
        }

        // Legal-person fields. Set on an individual they would simply be unused, so they are not
        // refused: the one caller is a legal-entity screen, and a type check here would be a
        // second place to keep in step with ClientType.
        if (corporatePurpose is not null) CorporatePurpose = Clean(corporatePurpose);
        if (registrationExtractIssuedOn is not null)
            RegistrationExtractIssuedOn = registrationExtractIssuedOn;

        Touch(actor);
        return Result.Ok();
    }

    /// <summary>
    /// Whether the registry extract on file satisfies article 13.2's three-month rule
    /// <paramref name="asOf"/>. <c>false</c> when none is recorded — an absent document does not
    /// satisfy a requirement for a recent one.
    ///
    /// <para>
    /// The single definition, so the creation path and the KYC validation path cannot grow two
    /// different notions of "recent". <paramref name="maxAgeMonths"/> is passed in rather than
    /// fixed at three: the figure belongs to a tenant parameter, against a reforming text.
    /// </para>
    /// </summary>
    public bool HasFreshRegistrationExtract(DateOnly asOf, int maxAgeMonths)
        => RegistrationExtractIssuedOn is { } issued
        && issued <= asOf
        && issued >= asOf.AddMonths(-maxAgeMonths);

    public Result UpdateSensitiveIdentity(
        string? firstName,
        string? lastName,
        string? maidenName,
        IdentityDocumentType? docType,
        string? encryptedDocNumber,
        string? docNumberBlindIndex,
        DateOnly? docIssuedOn,
        DateOnly? docExpiresOn,
        Guid actor)
    {
        if (IsReadOnly)
            return Result.Fail(CustomerErrors.ClientReadOnly);

        var changed = new List<string>();

        if (!string.IsNullOrWhiteSpace(firstName) && firstName.Trim() != FirstName)
        {
            FirstName = firstName.Trim();
            changed.Add(nameof(FirstName));
        }

        if (!string.IsNullOrWhiteSpace(lastName) && lastName.Trim() != LastName)
        {
            LastName = lastName.Trim();
            changed.Add(nameof(LastName));
        }

        if (maidenName is not null && Clean(maidenName) != MaidenName)
        {
            MaidenName = Clean(maidenName);
            changed.Add(nameof(MaidenName));
        }

        if (docType is not null && docType != IdentityDocumentType)
        {
            IdentityDocumentType = docType;
            changed.Add(nameof(IdentityDocumentType));
        }

        if (encryptedDocNumber is not null && encryptedDocNumber != EncryptedIdentityDocumentNumber)
        {
            EncryptedIdentityDocumentNumber = encryptedDocNumber;
            IdentityDocumentNumberBlindIndex = docNumberBlindIndex;
            changed.Add(nameof(SensitiveField.IdentityDocumentNumber));
        }

        if (docIssuedOn is not null && docIssuedOn != IdentityDocumentIssuedOn)
        {
            IdentityDocumentIssuedOn = docIssuedOn;
            changed.Add(nameof(IdentityDocumentIssuedOn));
        }

        if (docExpiresOn is not null && docExpiresOn != IdentityDocumentExpiresOn)
        {
            IdentityDocumentExpiresOn = docExpiresOn;
            changed.Add(nameof(IdentityDocumentExpiresOn));
        }

        SetDisplayName(
            BuildPersonDisplayName(FirstName, LastName),
            SearchKeyBuilder.ForPerson(FirstName, LastName));
        Touch(actor);

        if (changed.Count > 0)
            RaiseDomainEvent(new ClientSensitiveFieldsChangedDomainEvent(Id, changed));

        return Result.Ok();
    }

    public Result UpdateLegalIdentity(
        string? legalName,
        string? legalFormCode,
        string? encryptedRegistrationNumber,
        string? registrationNumberBlindIndex,
        string? encryptedTaxIdNumber,
        DateOnly? incorporationDate,
        Guid actor)
    {
        if (IsReadOnly)
            return Result.Fail(CustomerErrors.ClientReadOnly);

        var changed = new List<string>();

        if (!string.IsNullOrWhiteSpace(legalName) && legalName.Trim() != LegalName)
        {
            LegalName = legalName.Trim();
            SetDisplayName(LegalName, SearchKeyBuilder.ForLegalEntity(LegalName));
            changed.Add(nameof(LegalName));
        }

        if (!string.IsNullOrWhiteSpace(legalFormCode) && legalFormCode.Trim() != LegalFormCode)
        {
            LegalFormCode = legalFormCode.Trim();
            changed.Add(nameof(LegalFormCode));
        }

        if (encryptedRegistrationNumber is not null && encryptedRegistrationNumber != EncryptedRegistrationNumber)
        {
            EncryptedRegistrationNumber = encryptedRegistrationNumber;
            RegistrationNumberBlindIndex = registrationNumberBlindIndex;
            changed.Add(nameof(SensitiveField.RegistrationNumber));
        }

        if (encryptedTaxIdNumber is not null && encryptedTaxIdNumber != EncryptedTaxIdNumber)
        {
            EncryptedTaxIdNumber = encryptedTaxIdNumber;
            changed.Add(nameof(SensitiveField.TaxIdNumber));
        }

        if (incorporationDate is not null && incorporationDate != IncorporationDate)
        {
            IncorporationDate = incorporationDate;
            changed.Add(nameof(IncorporationDate));
        }

        Touch(actor);

        if (changed.Count > 0)
            RaiseDomainEvent(new ClientSensitiveFieldsChangedDomainEvent(Id, changed));

        return Result.Ok();
    }

    // ── KYC ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// KYC approval. A client waiting for KYC becomes <c>Active</c>; for any other status
    /// (including the read-only ones) only the KYC status itself is refreshed, because a KYC
    /// decision that lands late must never resurrect a suspended or archived client.
    /// </summary>
    public Result ApplyKycValidated(DateTimeOffset at, Guid actor)
    {
        KycStatus = KycStatus.Approved;
        KycRejectionReason = null;
        KycUpdatedAt = at;

        if (Status == ClientStatus.PendingKyc)
            ChangeStatus(ClientStatus.Active, null, actor, at);
        else
            Touch(actor, at);

        return Result.Ok();
    }

    public Result ApplyKycRejected(string reason, DateTimeOffset at, Guid actor)
    {
        if (string.IsNullOrWhiteSpace(reason))
            return Result.Fail(CustomerErrors.ReasonRequired);

        KycStatus = KycStatus.Rejected;
        KycRejectionReason = reason.Trim();
        KycUpdatedAt = at;

        if (Status == ClientStatus.PendingKyc)
            ChangeStatus(ClientStatus.KycRejected, reason.Trim(), actor, at);
        else
            Touch(actor, at);

        return Result.Ok();
    }

    /// <summary>
    /// A client converted on a product that requires no KYC (a Standard product whose policy says
    /// <c>None</c>): active at once, its KYC status left at <c>NotStarted</c> — no file was opened,
    /// none was approved. A later product that does require KYC opens a file then, and the
    /// per-product gate checks that file; the client's own status does not stand in for it.
    /// </summary>
    public Result ActivateWithoutKyc(Guid actor, DateTimeOffset at)
    {
        if (Status != ClientStatus.PendingKyc)
            return Result.Fail(CustomerErrors.InvalidStatusTransition);

        ChangeStatus(ClientStatus.Active, "KYC non requis par le produit", actor, at);
        return Result.Ok();
    }

    /// <summary>
    /// Un dossier KYC vient d'être ouvert. Ne fait avancer que ce qui n'est pas encore tranché :
    /// un client déjà validé dont on ouvre un dossier de revue reste validé — une revue ne le
    /// dévalide pas, comme le statut public <c>UnderReview</c> le dit déjà côté KYC. Renvoie vrai
    /// quand le statut a changé.
    /// </summary>
    public bool ApplyKycInitiated(DateTimeOffset at)
    {
        if (KycStatus is not (KycStatus.NotStarted or KycStatus.Rejected or KycStatus.Expired))
            return false;

        KycStatus = KycStatus.InProgress;
        KycUpdatedAt = at;
        UpdatedAt = at;
        return true;
    }

    public void ApplyRiskLevel(RiskLevel level, DateTimeOffset at)
    {
        RiskLevel = level;
        KycUpdatedAt = at;
        UpdatedAt = at;
    }

    // ── Lifecycle ───────────────────────────────────────────────────────────

    public Result Suspend(string reason, Guid actor)
    {
        if (IsReadOnly)
            return Result.Fail(CustomerErrors.ClientReadOnly);
        if (string.IsNullOrWhiteSpace(reason))
            return Result.Fail(CustomerErrors.ReasonRequired);
        if (Status != ClientStatus.Active)
            return Result.Fail(CustomerErrors.InvalidStatusTransition);

        ChangeStatus(ClientStatus.Suspended, reason.Trim(), actor, DateTimeOffset.UtcNow);
        return Result.Ok();
    }

    /// <summary>Back to <c>Active</c> when KYC is approved, otherwise back into the KYC queue.</summary>
    public Result Reactivate(Guid actor)
    {
        if (IsReadOnly)
            return Result.Fail(CustomerErrors.ClientReadOnly);
        if (Status != ClientStatus.Suspended)
            return Result.Fail(CustomerErrors.InvalidStatusTransition);

        var target = KycStatus == KycStatus.Approved ? ClientStatus.Active : ClientStatus.PendingKyc;
        ChangeStatus(target, null, actor, DateTimeOffset.UtcNow);
        return Result.Ok();
    }

    public Result Archive(string reason, Guid actor)
    {
        if (IsReadOnly)
            return Result.Fail(CustomerErrors.ClientReadOnly);
        if (string.IsNullOrWhiteSpace(reason))
            return Result.Fail(CustomerErrors.ReasonRequired);

        var now = DateTimeOffset.UtcNow;
        ArchiveReason = reason.Trim();
        ArchivedAt = now;
        ChangeStatus(ClientStatus.Archived, ArchiveReason, actor, now);
        return Result.Ok();
    }

    public Result AssignAdvisor(Guid? advisorUserId, Guid actor)
    {
        if (IsReadOnly)
            return Result.Fail(CustomerErrors.ClientReadOnly);

        AdvisorUserId = advisorUserId;
        Touch(actor);
        return Result.Ok();
    }

    /// <summary>
    /// Moves the client to another agency. With <paramref name="keepAdvisor"/> false the advisor is
    /// cleared, because an advisor belongs to the agency the client just left.
    /// </summary>
    public Result TransferToAgency(Guid agencyId, string agencyCode, bool keepAdvisor, Guid actor)
    {
        if (IsReadOnly)
            return Result.Fail(CustomerErrors.ClientReadOnly);
        if (agencyId == Guid.Empty)
            return Result.Fail(CustomerErrors.AgencyOutOfScope);
        if (string.IsNullOrWhiteSpace(agencyCode))
            return Result.Fail(CustomerErrors.AgencyOutOfScope);

        var previousAgencyId = AgencyId;

        AgencyId = agencyId;
        AgencyCode = agencyCode.Trim();
        if (!keepAdvisor)
            AdvisorUserId = null;

        Touch(actor);

        if (previousAgencyId != agencyId)
            RaiseDomainEvent(new ClientTransferredDomainEvent(Id, previousAgencyId, agencyId));

        return Result.Ok();
    }

    /// <summary>Marks this client as absorbed by <paramref name="survivorId"/>; terminal state.</summary>
    public Result MarkMerged(Guid survivorId, Guid actor)
    {
        if (survivorId == Guid.Empty || survivorId == Id)
            return Result.Fail(CustomerErrors.SameClientMergeForbidden);
        if (Status == ClientStatus.Merged)
            return Result.Fail(CustomerErrors.ClientAlreadyMerged);
        if (IsReadOnly)
            return Result.Fail(CustomerErrors.ClientReadOnly);

        MergedIntoId = survivorId;
        ChangeStatus(ClientStatus.Merged, null, actor, DateTimeOffset.UtcNow);
        RaiseDomainEvent(new ClientMergedDomainEvent(Id, survivorId));
        return Result.Ok();
    }

    // ── Segmentation, loyalty, dependents ───────────────────────────────────

    public void SetSegment(string segmentCode, DateTimeOffset at)
    {
        SegmentCode = Clean(segmentCode);
        SegmentAssignedAt = at;
        UpdatedAt = at;
    }

    public void SetLoyaltyScore(int score, bool provisional, DateTimeOffset at)
    {
        LoyaltyScore = Math.Clamp(score, 0, 100);
        LoyaltyScoreProvisional = provisional;
        LoyaltyScoreAt = at;
        UpdatedAt = at;
    }

    public void RecomputeDependentsCount(int count) => DependentsCount = Math.Max(0, count);

    // ── PEP (BNF-B-13) ──────────────────────────────────────────────────────

    /// <summary>
    /// Declares the client's own PEP status. An explicit "not exposed" IS a declaration.
    /// </summary>
    public Result DeclarePepStatus(PepStatus pep, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(pep);

        if (IsReadOnly)
            return Result.Fail(CustomerErrors.ClientReadOnly);

        PrincipalPep = pep;
        PepDeclaredAt = now;
        UpdatedAt = now;
        return Result.Ok();
    }

    /// <summary>
    /// Recomputes <see cref="HasPepExposure"/> over the client and their household, and raises
    /// <c>ClientPepExposureRaised</c> when the answer went from false to TRUE.
    /// </summary>
    /// <param name="householdExposed">
    /// Whether any ACTIVE household member is exposed at <paramref name="asOf"/>. Passed in rather
    /// than read here: the members are a separate aggregate, and <c>PepStatus.IsExposedAt</c> needs
    /// the tenant's post-mandate window, which a client does not know.
    /// </param>
    /// <returns>
    /// True when the flag ROSE. Only a rise is worth an event: BNF-B-13 asks for a KYC review when
    /// exposure appears, and firing on a fall would ask the compliance officer to re-review a file
    /// because a mandate expired — the opposite of the intent.
    /// </returns>
    public bool RecalculateHasPepExposure(
        bool householdExposed, DateOnly asOf, int postMandateMonths, DateTimeOffset now)
    {
        var exposed = PrincipalPep.IsExposedAt(asOf, postMandateMonths) || householdExposed;

        if (exposed == HasPepExposure)
            return false;

        var rose = exposed && !HasPepExposure;
        HasPepExposure = exposed;
        UpdatedAt = now;

        if (rose)
            RaiseDomainEvent(new ClientPepExposureRaisedDomainEvent(Id, TenantId));

        return rose;
    }

    /// <summary>
    /// Irreversible GDPR-style erasure: identity, encrypted payloads, blind indexes, phonetic keys
    /// and every contact point are dropped. The client number and the aggregate stay so that
    /// accounting history keeps a stable reference.
    /// </summary>
    public void Anonymize(Guid actor, DateTimeOffset at)
    {
        FirstName = null;
        LastName = null;
        MaidenName = null;
        Gender = null;
        BirthPlace = null;
        Nationality = null;
        MaritalStatus = null;
        FatherName = null;
        MotherName = null;
        Profession = null;
        Employer = null;
        LegalName = null;
        CustomFieldsJson = null;

        EncryptedDateOfBirth = null;
        DateOfBirthBlindIndex = null;
        EncryptedDeclaredIncome = null;
        DeclaredIncomeCurrency = null;
        EncryptedIdentityDocumentNumber = null;
        IdentityDocumentNumberBlindIndex = null;
        IdentityDocumentType = null;
        IdentityDocumentIssuedOn = null;
        IdentityDocumentExpiresOn = null;
        EncryptedRegistrationNumber = null;
        RegistrationNumberBlindIndex = null;
        EncryptedTaxIdNumber = null;

        PhoneticKeyPrimary = null;
        PhoneticKeySecondary = null;

        foreach (var contactPoint in _contactPoints.Where(cp => cp.IsActive).ToList())
            contactPoint.Close(at);

        SetDisplayName($"ANONYMIZED-{ClientNumber}", SearchKeyBuilder.NormalizeTerm($"ANONYMIZED {ClientNumber}"));
        IsAnonymized = true;
        UpdatedBy = actor;
        UpdatedAt = at;
    }

    // ── Contact points ──────────────────────────────────────────────────────

    /// <summary>
    /// Adds a contact point. Throws when the client is read-only: callers check
    /// <see cref="IsReadOnly"/> first and return <see cref="CustomerErrors.ClientReadOnly"/>.
    /// </summary>
    public ClientContactPoint AddContactPoint(
        ContactPointType type,
        string encryptedValue,
        string blindIndex,
        string? label,
        bool isPrimary,
        DateTimeOffset validFrom,
        Guid actor)
    {
        if (IsReadOnly)
            throw new DomainException("Client is read-only.", "Client.ReadOnly");
        if (string.IsNullOrWhiteSpace(encryptedValue))
            throw new DomainException("Contact point value is required.", "ClientContactPoint.Value.Required");
        if (string.IsNullOrWhiteSpace(blindIndex))
            throw new DomainException("Contact point blind index is required.", "ClientContactPoint.BlindIndex.Required");

        // The first contact point of a type is always the primary one.
        var promote = isPrimary || !_contactPoints.Any(cp => cp.IsActive && cp.Type == type);
        if (promote)
            DemotePrimary(type);

        var contactPoint = ClientContactPoint.Create(
            TenantId, Id, type, encryptedValue, blindIndex, Clean(label), promote, validFrom, actor);

        _contactPoints.Add(contactPoint);
        Touch(actor);
        return contactPoint;
    }

    /// <summary>Closes a contact point. A client must always keep at least one active phone.</summary>
    public Result CloseContactPoint(Guid contactPointId, Guid actor, DateTimeOffset at)
    {
        if (IsReadOnly)
            return Result.Fail(CustomerErrors.ClientReadOnly);

        var contactPoint = _contactPoints.FirstOrDefault(cp => cp.Id == contactPointId && cp.IsActive);
        if (contactPoint is null)
            return Result.Fail(CustomerErrors.ContactPointNotFound);

        if (contactPoint.Type == ContactPointType.Phone
            && _contactPoints.Count(cp => cp.IsActive && cp.Type == ContactPointType.Phone) <= 1)
        {
            return Result.Fail(CustomerErrors.LastPhoneRequired);
        }

        contactPoint.Close(at);

        // Never leave a type without a primary.
        if (contactPoint.IsPrimary)
        {
            contactPoint.SetPrimary(false);
            var replacement = _contactPoints
                .Where(cp => cp.IsActive && cp.Type == contactPoint.Type)
                .OrderBy(cp => cp.CreatedAt)
                .FirstOrDefault();
            replacement?.SetPrimary(true);
        }

        Touch(actor, at);
        return Result.Ok();
    }

    /// <summary>Promotes a contact point, demoting the previous primary of the SAME type only.</summary>
    public Result PromoteContactPointToPrimary(Guid contactPointId, Guid actor)
    {
        if (IsReadOnly)
            return Result.Fail(CustomerErrors.ClientReadOnly);

        var contactPoint = _contactPoints.FirstOrDefault(cp => cp.Id == contactPointId && cp.IsActive);
        if (contactPoint is null)
            return Result.Fail(CustomerErrors.ContactPointNotFound);

        DemotePrimary(contactPoint.Type);
        contactPoint.SetPrimary(true);
        Touch(actor);
        return Result.Ok();
    }

    /// <summary>Re-parents a contact point coming from an absorbed client (merge execution).</summary>
    internal void AttachContactPoint(ClientContactPoint cp)
    {
        ArgumentNullException.ThrowIfNull(cp);

        cp.ReassignTo(Id);
        // The survivor keeps its own primaries: an imported contact point is never primary.
        if (cp.IsPrimary && _contactPoints.Any(existing => existing.IsActive && existing.Type == cp.Type && existing.IsPrimary))
            cp.SetPrimary(false);

        _contactPoints.Add(cp);
    }

    internal void RecordStatusTransition(ClientStatus from, ClientStatus to, string? reason, Guid actor)
    {
        var now = DateTimeOffset.UtcNow;
        _statusHistory.Add(ClientStatusHistory.Create(TenantId, Id, from, to, reason, actor, now));
        RaiseDomainEvent(new ClientStatusChangedDomainEvent(Id, from, to, reason));
    }

    // ── Internals ───────────────────────────────────────────────────────────

    private void ChangeStatus(ClientStatus target, string? reason, Guid actor, DateTimeOffset at)
    {
        var previous = Status;
        Status = target;
        _statusHistory.Add(ClientStatusHistory.Create(TenantId, Id, previous, target, reason, actor, at));
        RaiseDomainEvent(new ClientStatusChangedDomainEvent(Id, previous, target, reason));
        Touch(actor, at);
    }

    private void DemotePrimary(ContactPointType type)
    {
        foreach (var cp in _contactPoints.Where(cp => cp.IsActive && cp.Type == type && cp.IsPrimary).ToList())
            cp.SetPrimary(false);
    }

    private void Touch(Guid actor, DateTimeOffset? at = null)
    {
        UpdatedBy = actor;
        UpdatedAt = at ?? DateTimeOffset.UtcNow;
    }

    /// <summary>
    /// Single write path for the label and its search projection, so the two can
    /// never drift apart.
    /// </summary>
    private void SetDisplayName(string displayName, string searchKey)
    {
        DisplayName = displayName;
        SearchKey = searchKey;
    }

    private static string BuildPersonDisplayName(string? firstName, string? lastName) =>
        string.Join(' ', new[] { firstName, lastName }
            .Where(part => !string.IsNullOrWhiteSpace(part))
            .Select(part => part!.Trim()));

    /// <summary>System default language code, lower-case like every seeded email template.</summary>
    public const string DefaultLanguageCode = LanguageCode.Default;

    /// <summary>
    /// Lower-cases the language and keeps only its primary subtag, so "FR", "Fr", "fr-FR" and
    /// "fr_FR" are stored as one value.
    ///
    /// This used to trim only — and defaulted to "FR" upper-case — which put "fr", "Fr" and "FR"
    /// in the same column as three distinct languages. The visible consequence was an email:
    /// template locales are stored lower-case and resolved in PostgreSQL, where equality is
    /// case-sensitive, so a client whose language read "FR" matched no template and received the
    /// message's own JSON payload as its body.
    /// </summary>
    private static string DefaultLanguage(string? preferredLanguage)
        => LanguageCode.Normalize(preferredLanguage);

    private static string? Clean(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
