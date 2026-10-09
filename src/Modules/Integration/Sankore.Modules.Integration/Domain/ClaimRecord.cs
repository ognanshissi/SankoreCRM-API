namespace Sankore.Modules.Integration.Domain;

using Sankore.Modules.Integration.PublicApi;
using Sankore.Shared.Kernel;

/// <summary>
/// One claim (<i>sinistre</i>) declared for a policy, and its follow-up (ASS-09).
///
/// <para>
/// <c>ClaimRecord</c> and not <c>InsuranceClaim</c>, for the Swashbuckle reason
/// <see cref="PolicyRecord"/> explains: <see cref="PublicApi.InsuranceClaim"/> is the projection
/// consumers receive and OpenAPI components are keyed by simple name.
/// </para>
///
/// <para>
/// <see cref="CrmCustomerId"/> is denormalised from the policy on purpose. Every read of this
/// table is scoped by customer — "my client's claims" at the counter, and the agency-perimeter
/// check that must answer not-found rather than 403 — and a join to <c>ins_policy</c> on every one
/// of them would make the perimeter check depend on a second row still being there.
/// </para>
/// </summary>
public sealed class ClaimRecord : AggregateRoot
{
    public Guid Id { get; private set; }

    /// <summary>The insurer. Intra-schema FK.</summary>
    public Guid ConnectionId { get; private set; }

    /// <summary>Intra-schema FK to <c>ins_policy</c>: a claim without a contract is meaningless.</summary>
    public Guid PolicyId { get; private set; }

    /// <summary>Opaque reference to M01's customer. See the class remarks.</summary>
    public Guid CrmCustomerId { get; private set; }

    public DateOnly OccurredOn { get; private set; }

    /// <summary>
    /// The nature of the loss, as a short code or label. In clear, and part of
    /// <c>ux_ins_claim_declaration</c>: it is the discriminator that lets a fire and a theft on
    /// the same policy on the same day be two claims, exactly as
    /// <c>IdempotencyKeyFactory.ForClaimDeclaration</c> already decides for the command.
    /// </summary>
    public string Nature { get; private set; } = string.Empty;

    /// <summary>
    /// The agent's account of the loss, encrypted (AES-256-GCM, this module's key).
    ///
    /// <para>
    /// ASS-12 names « les pièces de sinistre » and this is the narrative that goes with them: a
    /// death, an illness, an accident, in free text about a named person. Encrypted for the same
    /// reason the documents are, and <c>text</c> with no cap because the ciphertext grows with the
    /// account. Nothing searches it — no criterion asks to — so the cost of encrypting it is
    /// nothing.
    /// </para>
    /// </summary>
    public string? DescriptionEncrypted { get; private set; }

    public ClaimStatus Status { get; private set; }

    /// <summary>What the insurer says about the current status. Never a payload value.</summary>
    public string? StatusDetail { get; private set; }

    public DateTimeOffset StatusChangedAt { get; private set; }

    /// <summary>
    /// The documents the insurer is waiting for (ASS-09, criterion 4). Populated when the status
    /// becomes <see cref="ClaimStatus.DocumentsRequired"/>; the agent task is raised by the slice
    /// that sees the transition, not by this row.
    /// </summary>
    public string? MissingDocuments { get; private set; }

    /// <summary>The insurer's dossier identifier, once it answers (ASS-09, criterion 2).</summary>
    public string? ExternalClaimId { get; private set; }

    /// <summary>The human-readable dossier number the customer is given.</summary>
    public string? ClaimNumber { get; private set; }

    /// <summary>The <c>DeclareClaim</c> command that carried it. Plain Guid, filtered index, no FK.</summary>
    public Guid? DeclareCommandId { get; private set; }

    public DateTimeOffset DeclaredAt { get; private set; }

    /// <summary>Who declared it. An agent, always — the claim screen is ASS-09's.</summary>
    public Guid DeclaredBy { get; private set; }

    // ── Indemnity (ASS-09, criterion 5) ─────────────────────────────────────

    public decimal? IndemnityAmount { get; private set; }

    public string? IndemnityCurrency { get; private set; }

    /// <summary>
    /// The CBS reference of the credit, when the indemnity was paid into the customer's account.
    /// Kept on the claim so « le versement apparaît dans le dossier » is one read and not a search
    /// through the transaction history of an account.
    /// </summary>
    public string? IndemnityCbsReference { get; private set; }

    public DateTimeOffset? IndemnityPaidAt { get; private set; }

    public DateTimeOffset? LastSyncedAt { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }

    public uint Version { get; private set; }

    private ClaimRecord() { }

    public static ClaimRecord Create(
        Guid tenantId,
        Guid connectionId,
        Guid policyId,
        Guid crmCustomerId,
        DateOnly occurredOn,
        string nature,
        Guid declaredBy,
        TimeProvider clock,
        string? descriptionEncrypted = null,
        Guid? id = null)
    {
        if (tenantId == Guid.Empty) throw new DomainException("TenantId is required.");
        if (connectionId == Guid.Empty) throw new DomainException("ConnectionId is required.");
        if (policyId == Guid.Empty) throw new DomainException("PolicyId is required.");
        if (crmCustomerId == Guid.Empty) throw new DomainException("CrmCustomerId is required.");
        if (string.IsNullOrWhiteSpace(nature))
            throw new DomainException("A claim needs the nature of the loss.");
        ArgumentNullException.ThrowIfNull(clock);

        var now = clock.GetUtcNow();

        return new ClaimRecord
        {
            Id = id ?? Guid.NewGuid(),
            TenantId = tenantId,
            ConnectionId = connectionId,
            PolicyId = policyId,
            CrmCustomerId = crmCustomerId,
            OccurredOn = occurredOn,
            Nature = nature.Trim(),
            DescriptionEncrypted = descriptionEncrypted,
            Status = ClaimStatus.Declared,
            StatusChangedAt = now,
            DeclaredAt = now,
            DeclaredBy = declaredBy,
            CreatedAt = now,
            UpdatedAt = now,
        };
    }

    public void AttachDeclareCommand(Guid commandId, TimeProvider clock)
    {
        ArgumentNullException.ThrowIfNull(clock);

        DeclareCommandId = commandId;
        UpdatedAt = clock.GetUtcNow();
    }

    /// <summary>
    /// The insurer acknowledged the declaration and gave its dossier reference. Refuses to
    /// re-point an existing reference: that would move a dossier between claims.
    /// </summary>
    public void RecordInsurerReference(
        string externalClaimId, string? claimNumber, TimeProvider clock)
    {
        if (string.IsNullOrWhiteSpace(externalClaimId))
            throw new DomainException("The insurer's claim reference is required.");

        var trimmed = externalClaimId.Trim();

        if (ExternalClaimId is not null && !string.Equals(ExternalClaimId, trimmed, StringComparison.Ordinal))
            throw new DomainException(
                "This claim already carries another insurer reference; re-pointing it would move "
                + "a dossier between claims.");

        ExternalClaimId = trimmed;
        ClaimNumber = claimNumber?.Trim();
        UpdatedAt = clock.GetUtcNow();
    }

    /// <summary>
    /// Applies the insurer's reported state, and answers whether it actually moved — the same
    /// contract as <see cref="PolicyRecord.ApplySync"/>, and for the same reason: ASS-09's third
    /// criterion wants an event per CHANGE, and a nightly sync over a portfolio would otherwise
    /// publish one per claim per night.
    /// </summary>
    public bool ApplySync(
        ClaimStatus status,
        string? statusDetail,
        string? missingDocuments,
        TimeProvider clock,
        decimal? indemnityAmount = null,
        string? indemnityCurrency = null)
    {
        ArgumentNullException.ThrowIfNull(clock);

        var now = clock.GetUtcNow();
        var moved = status != Status;

        if (moved)
        {
            Status = status;
            StatusChangedAt = now;
        }

        StatusDetail = statusDetail;

        // Cleared when the insurer stops asking: a stale list of missing documents keeps an agent
        // task alive for a dossier that has moved on.
        MissingDocuments = status == ClaimStatus.DocumentsRequired ? missingDocuments : null;

        if (indemnityAmount is not null)
        {
            IndemnityAmount = indemnityAmount;
            IndemnityCurrency = indemnityCurrency?.Trim().ToUpperInvariant() ?? IndemnityCurrency;
        }

        LastSyncedAt = now;
        UpdatedAt = now;

        return moved;
    }

    /// <summary>
    /// The indemnity reached the customer's CBS account (ASS-09, criterion 5). Separate from
    /// <see cref="ApplySync"/>: the insurer tells us the amount, the CBS tells us the credit, and
    /// the two arrive from different systems at different times.
    /// </summary>
    public void RecordIndemnityPayment(
        decimal amount, string currency, string? cbsReference, TimeProvider clock)
    {
        if (amount <= 0m) throw new DomainException("An indemnity payment needs a positive amount.");
        if (string.IsNullOrWhiteSpace(currency))
            throw new DomainException("An indemnity amount requires its currency.");
        ArgumentNullException.ThrowIfNull(clock);

        IndemnityAmount = amount;
        IndemnityCurrency = currency.Trim().ToUpperInvariant();
        IndemnityCbsReference = cbsReference?.Trim();
        IndemnityPaidAt = clock.GetUtcNow();

        // The status follows the money: an indemnified claim is terminal whatever the last sync
        // said, because the customer has been paid.
        if (Status != ClaimStatus.Indemnified)
        {
            Status = ClaimStatus.Indemnified;
            StatusChangedAt = IndemnityPaidAt.Value;
        }

        UpdatedAt = IndemnityPaidAt.Value;
    }

    /// <summary>True while the insurer may still ask for something or change its mind.</summary>
    public bool IsOpen => Status is ClaimStatus.Declared
        or ClaimStatus.UnderReview
        or ClaimStatus.DocumentsRequired
        or ClaimStatus.Accepted;
}

/// <summary>
/// One supporting document of a claim (ASS-09, criterion 1).
///
/// <para>
/// <b>No bytes column.</b> The content is an encrypted object in the module's file store —
/// file-level encryption, the branch ASS-12 allows for <i>pièces de sinistre</i> — and the row
/// keeps the reference, the declared type, the size and the digest. A photograph of a wrecked
/// vehicle does not belong in a relational column, and a <c>bytea</c> here would make every list
/// of a dossier's documents a multi-megabyte read.
/// </para>
///
/// <para>
/// <see cref="ScanStatus"/> starts at <see cref="DocumentScanStatus.Pending"/> and
/// <see cref="IsTransmittable"/> is false until it is <see cref="DocumentScanStatus.Clean"/>. The
/// criterion asks for an antivirus pass; what makes it meaningful is that nothing may leave for
/// the insurer before it has run, so the gate is a property of this row and not a step somebody
/// remembers to call.
/// </para>
/// </summary>
public sealed class ClaimDocument : AggregateRoot
{
    public Guid Id { get; private set; }

    /// <summary>Intra-schema FK to <c>ins_claim</c>, cascading: a document is part of its dossier.</summary>
    public Guid ClaimId { get; private set; }

    /// <summary>What the document is — <c>PoliceReport</c>, <c>MedicalCertificate</c>, an insurer code.</summary>
    public string DocumentKind { get; private set; } = string.Empty;

    public string FileName { get; private set; } = string.Empty;

    public string ContentType { get; private set; } = string.Empty;

    public long SizeBytes { get; private set; }

    /// <summary>
    /// SHA-256 of the PLAINTEXT, lower-case hex. Part of <c>ux_ins_claim_document_digest</c>, so
    /// the same file uploaded twice for one dossier is refused rather than sent to the insurer
    /// twice.
    /// </summary>
    public string Sha256 { get; private set; } = string.Empty;

    /// <summary>Reference of the encrypted object. Never a caller-supplied path.</summary>
    public string StorageRef { get; private set; } = string.Empty;

    public DocumentScanStatus ScanStatus { get; private set; }

    public DateTimeOffset? ScannedAt { get; private set; }

    /// <summary>What the scanner reported. A signature name, never the file's content.</summary>
    public string? ScanDetail { get; private set; }

    /// <summary>
    /// True when the insurer asked for this document after the declaration — the case ASS-09's
    /// fourth criterion raises an agent task for. Distinguishes a complement from a document the
    /// agent supplied up front, which matters when a dossier is reviewed months later.
    /// </summary>
    public bool RequestedByInsurer { get; private set; }

    public Guid UploadedBy { get; private set; }

    public DateTimeOffset UploadedAt { get; private set; }

    /// <summary>When it reached the insurer, and through which command.</summary>
    public DateTimeOffset? TransmittedAt { get; private set; }

    public Guid? TransmitCommandId { get; private set; }

    public uint Version { get; private set; }

    private ClaimDocument() { }

    public static ClaimDocument Create(
        Guid tenantId,
        Guid claimId,
        string documentKind,
        string fileName,
        string contentType,
        long sizeBytes,
        string sha256,
        string storageRef,
        Guid uploadedBy,
        TimeProvider clock,
        bool requestedByInsurer = false,
        Guid? id = null)
    {
        if (tenantId == Guid.Empty) throw new DomainException("TenantId is required.");
        if (claimId == Guid.Empty) throw new DomainException("ClaimId is required.");
        if (string.IsNullOrWhiteSpace(documentKind))
            throw new DomainException("A document kind is required.");
        if (string.IsNullOrWhiteSpace(storageRef))
            throw new DomainException("A document row with no stored object is unreadable.");
        if (string.IsNullOrWhiteSpace(sha256))
            throw new DomainException("A document must carry the digest of what was stored.");
        if (sizeBytes <= 0) throw new DomainException("An empty document must not be recorded.");
        ArgumentNullException.ThrowIfNull(clock);

        return new ClaimDocument
        {
            Id = id ?? Guid.NewGuid(),
            TenantId = tenantId,
            ClaimId = claimId,
            DocumentKind = documentKind.Trim(),
            FileName = string.IsNullOrWhiteSpace(fileName) ? "piece" : fileName.Trim(),
            ContentType = string.IsNullOrWhiteSpace(contentType)
                ? "application/octet-stream"
                : contentType.Trim(),
            SizeBytes = sizeBytes,
            Sha256 = sha256.Trim().ToLowerInvariant(),
            StorageRef = storageRef.Trim(),
            // Pending, always. A document is never born clean: the scan has to have run.
            ScanStatus = DocumentScanStatus.Pending,
            RequestedByInsurer = requestedByInsurer,
            UploadedBy = uploadedBy,
            UploadedAt = clock.GetUtcNow(),
        };
    }

    public void RecordScan(DocumentScanStatus status, string? detail, TimeProvider clock)
    {
        if (status == DocumentScanStatus.Pending)
            throw new DomainException("A scan result cannot be 'Pending'.");

        ArgumentNullException.ThrowIfNull(clock);

        ScanStatus = status;
        ScanDetail = detail;
        ScannedAt = clock.GetUtcNow();
    }

    public void MarkTransmitted(Guid commandId, TimeProvider clock)
    {
        if (!IsTransmittable)
            throw new DomainException(
                $"A document whose scan status is {ScanStatus} must not be sent to the insurer.");

        ArgumentNullException.ThrowIfNull(clock);

        TransmitCommandId = commandId;
        TransmittedAt = clock.GetUtcNow();
    }

    /// <summary>The gate. Only a scanned, clean document may leave the platform.</summary>
    public bool IsTransmittable => ScanStatus == DocumentScanStatus.Clean;
}
