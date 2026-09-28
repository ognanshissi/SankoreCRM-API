namespace Sankore.Modules.Customers.Domain;

using Sankore.Modules.Customers.Domain.Matching;
using Sankore.Modules.Customers.Domain.Events;
using Sankore.Modules.Kyc.PublicApi;
using Sankore.Shared.Kernel;

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
        if (!string.IsNullOrWhiteSpace(preferredLanguage)) PreferredLanguage = preferredLanguage.Trim();

        Touch(actor);
        return Result.Ok();
    }

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

    private static string DefaultLanguage(string? preferredLanguage) =>
        string.IsNullOrWhiteSpace(preferredLanguage) ? "FR" : preferredLanguage.Trim();

    private static string? Clean(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
