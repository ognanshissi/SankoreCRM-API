namespace Sankore.Modules.Integration.Domain;

using Sankore.Modules.Integration.PublicApi;
using Sankore.Shared.Kernel;

/// <summary>
/// The read model of one policy at one insurer (ASS-07).
///
/// <para>
/// Named <c>PolicyRecord</c> in code and <c>ins_policy</c> in the schema. The suffix is not
/// decoration: <see cref="PublicApi.InsurancePolicy"/> is the projection consumers receive, and
/// Swashbuckle keys OpenAPI components by SIMPLE NAME, so two types called <c>InsurancePolicy</c>
/// in one document collide — the same trap <see cref="CbsSnapshot"/> documents against
/// <see cref="PublicApi.CbsCustomerSnapshot"/>.
/// </para>
///
/// <para>
/// Written by two sources, which is why <see cref="InsuranceSubscription"/> is a separate table:
/// the subscription chain of ASS-04 when SANKORE sold the contract, and ASS-07's periodic
/// synchronisation for every policy the insurer holds for a customer we know — including the ones
/// sold at the insurer's own counter, which have no subscription here at all.
/// </para>
///
/// <para>
/// <see cref="ProductId"/> is therefore nullable. A synchronised policy may name an insurer
/// product the tenant's catalogue does not hold, and inventing a catalogue row to satisfy a
/// foreign key would put a product nobody configured in front of an agent.
/// </para>
/// </summary>
public sealed class PolicyRecord : AggregateRoot
{
    public Guid Id { get; private set; }

    /// <summary>The insurer. Intra-schema FK.</summary>
    public Guid ConnectionId { get; private set; }

    /// <summary>Opaque reference to M01's customer. No cross-schema foreign key.</summary>
    public Guid CrmCustomerId { get; private set; }

    /// <summary>The catalogue entry, when the policy matches one. See the class remarks.</summary>
    public Guid? ProductId { get; private set; }

    /// <summary>
    /// The insurer's own identifier for the contract — what every port call is keyed on. Part of
    /// <c>ux_ins_policy_external</c>, which is what makes the synchronisation an upsert.
    /// </summary>
    public string ExternalPolicyId { get; private set; } = string.Empty;

    /// <summary>
    /// The human-readable contract number printed on the attestation. Distinct from
    /// <see cref="ExternalPolicyId"/> because insurers routinely have both, and the one an agent
    /// reads out to a customer is not the one the API takes.
    /// </summary>
    public string PolicyNumber { get; private set; } = string.Empty;

    /// <summary>The insurer's product code as the policy carries it. Kept even when unmapped.</summary>
    public string InsurerProductCode { get; private set; } = string.Empty;

    public PolicyStatus Status { get; private set; }

    /// <summary>
    /// Why the policy is in its current status, when the insurer says — « suspendu pour impayé »
    /// being the case ASS-08's third criterion cares about. Never a payload value.
    /// </summary>
    public string? StatusDetail { get; private set; }

    /// <summary>
    /// When the status last changed. The field ASS-07's third criterion hangs on: the
    /// synchronisation publishes <c>PolicyStatusChangedEvent</c> only when the incoming status
    /// differs from the stored one, so a nightly sync of an unchanged policy notifies nobody.
    /// </summary>
    public DateTimeOffset StatusChangedAt { get; private set; }

    public DateOnly EffectiveDate { get; private set; }

    public DateOnly? ExpiryDate { get; private set; }

    /// <summary>The next premium due date as the insurer reports it (ASS-07, criterion 1).</summary>
    public DateOnly? NextDueDate { get; private set; }

    public decimal PremiumAmount { get; private set; }

    public string Currency { get; private set; } = string.Empty;

    public PremiumPeriodicity Periodicity { get; private set; }

    public decimal? InsuredAmount { get; private set; }

    /// <summary>The subscription that produced it, when SANKORE sold it. Null for a synced policy.</summary>
    public Guid? SubscriptionId { get; private set; }

    /// <summary>When the synchronisation last touched this row. Shown as the freshness of the screen.</summary>
    public DateTimeOffset LastSyncedAt { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }

    public uint Version { get; private set; }

    private PolicyRecord() { }

    public static PolicyRecord Create(
        Guid tenantId,
        Guid connectionId,
        Guid crmCustomerId,
        string externalPolicyId,
        string policyNumber,
        string insurerProductCode,
        PolicyStatus status,
        DateOnly effectiveDate,
        decimal premiumAmount,
        string currency,
        PremiumPeriodicity periodicity,
        TimeProvider clock,
        Guid? productId = null,
        Guid? subscriptionId = null,
        DateOnly? expiryDate = null,
        DateOnly? nextDueDate = null,
        decimal? insuredAmount = null,
        string? statusDetail = null,
        Guid? id = null)
    {
        if (tenantId == Guid.Empty) throw new DomainException("TenantId is required.");
        if (connectionId == Guid.Empty) throw new DomainException("ConnectionId is required.");
        if (crmCustomerId == Guid.Empty) throw new DomainException("CrmCustomerId is required.");
        if (string.IsNullOrWhiteSpace(externalPolicyId))
            throw new DomainException("A policy needs the insurer's own identifier.");
        if (string.IsNullOrWhiteSpace(currency))
            throw new DomainException("A premium amount requires its currency.");
        ArgumentNullException.ThrowIfNull(clock);

        var now = clock.GetUtcNow();

        return new PolicyRecord
        {
            Id = id ?? Guid.NewGuid(),
            TenantId = tenantId,
            ConnectionId = connectionId,
            CrmCustomerId = crmCustomerId,
            ProductId = productId,
            SubscriptionId = subscriptionId,
            ExternalPolicyId = externalPolicyId.Trim(),
            PolicyNumber = policyNumber?.Trim() ?? string.Empty,
            InsurerProductCode = insurerProductCode?.Trim() ?? string.Empty,
            Status = status,
            StatusDetail = statusDetail,
            StatusChangedAt = now,
            EffectiveDate = effectiveDate,
            ExpiryDate = expiryDate,
            NextDueDate = nextDueDate,
            PremiumAmount = premiumAmount,
            Currency = currency.Trim().ToUpperInvariant(),
            Periodicity = periodicity,
            InsuredAmount = insuredAmount,
            LastSyncedAt = now,
            CreatedAt = now,
            UpdatedAt = now,
        };
    }

    /// <summary>
    /// Applies what the insurer reports, and answers <b>whether the status actually moved</b>.
    ///
    /// <para>
    /// The return value is the point. ASS-07's third criterion wants an event on a status change,
    /// and a synchronisation that runs nightly over a portfolio would otherwise publish one per
    /// policy per night — which M08 turns into a notification to every customer, every night,
    /// about nothing. The caller publishes only when this returns <c>true</c>.
    /// </para>
    /// </summary>
    public bool ApplySync(
        PolicyStatus status,
        string policyNumber,
        DateOnly effectiveDate,
        DateOnly? expiryDate,
        DateOnly? nextDueDate,
        decimal premiumAmount,
        PremiumPeriodicity periodicity,
        TimeProvider clock,
        string? statusDetail = null,
        decimal? insuredAmount = null)
    {
        ArgumentNullException.ThrowIfNull(clock);

        var now = clock.GetUtcNow();
        var moved = status != Status;

        if (moved)
        {
            Status = status;
            StatusChangedAt = now;
        }

        // The detail is refreshed either way: an unchanged Suspended whose reason became
        // "impayé échéance 3" is new information for the agent even though the status did not move.
        StatusDetail = statusDetail;

        if (!string.IsNullOrWhiteSpace(policyNumber)) PolicyNumber = policyNumber.Trim();

        EffectiveDate = effectiveDate;
        ExpiryDate = expiryDate;
        NextDueDate = nextDueDate;
        PremiumAmount = premiumAmount;
        Periodicity = periodicity;
        InsuredAmount = insuredAmount ?? InsuredAmount;
        LastSyncedAt = now;
        UpdatedAt = now;

        return moved;
    }

    /// <summary>Links the catalogue entry once a mapping makes the product resolvable.</summary>
    public void LinkProduct(Guid productId, TimeProvider clock)
    {
        if (productId == Guid.Empty) throw new DomainException("ProductId is required.");
        ArgumentNullException.ThrowIfNull(clock);

        ProductId = productId;
        UpdatedAt = clock.GetUtcNow();
    }

    /// <summary>True while the contract can still carry a claim or a premium call.</summary>
    public bool IsLive => Status is PolicyStatus.Issued or PolicyStatus.Suspended;
}

/// <summary>
/// One attestation, as the insurer produced it (ASS-07, criterion 2).
///
/// <para>
/// <b>Its own table and no bytes column.</b> An attestation is a PDF: putting it on
/// <see cref="PolicyRecord"/> would drag it into every list of a customer's contracts, and putting
/// its bytes in any column would put a document store inside a relational row. The content goes to
/// the module's encrypted object store — file-level encryption, the second branch ASS-12 allows —
/// and the row keeps the reference, the type, the size and the digest.
/// </para>
///
/// <para>
/// <b>Several rows per policy are allowed, and that is deliberate.</b> A renewal produces a new
/// attestation, and the one handed to a customer last year is evidence of what they were given;
/// overwriting it would destroy that. The current attestation is the most recent
/// <see cref="FetchedAt"/>, served by <c>ix_ins_policy_certificate_policy_fetched</c>.
/// </para>
/// </summary>
public sealed class PolicyCertificate : AggregateRoot
{
    public Guid Id { get; private set; }

    /// <summary>Intra-schema FK to <c>ins_policy</c>, cascading: an attestation is part of its policy.</summary>
    public Guid PolicyId { get; private set; }

    /// <summary>The insurer's reference for this attestation, when it has one.</summary>
    public string? InsurerCertificateRef { get; private set; }

    public string FileName { get; private set; } = string.Empty;

    public string ContentType { get; private set; } = string.Empty;

    public long SizeBytes { get; private set; }

    /// <summary>SHA-256 of the PLAINTEXT document, lower-case hex: 64 characters.</summary>
    public string Sha256 { get; private set; } = string.Empty;

    /// <summary>Reference of the encrypted object. Never a path the caller supplied.</summary>
    public string StorageRef { get; private set; } = string.Empty;

    /// <summary>When it was fetched from the insurer. Orders the versions.</summary>
    public DateTimeOffset FetchedAt { get; private set; }

    /// <summary>Validity window the attestation itself states, when the insurer provides it.</summary>
    public DateOnly? ValidFrom { get; private set; }

    public DateOnly? ValidTo { get; private set; }

    private PolicyCertificate() { }

    public static PolicyCertificate Create(
        Guid tenantId,
        Guid policyId,
        string fileName,
        string contentType,
        long sizeBytes,
        string sha256,
        string storageRef,
        TimeProvider clock,
        string? insurerCertificateRef = null,
        DateOnly? validFrom = null,
        DateOnly? validTo = null,
        Guid? id = null)
    {
        if (tenantId == Guid.Empty) throw new DomainException("TenantId is required.");
        if (policyId == Guid.Empty) throw new DomainException("PolicyId is required.");
        if (string.IsNullOrWhiteSpace(storageRef))
            throw new DomainException("A certificate row with no stored object is unreadable.");
        if (string.IsNullOrWhiteSpace(sha256))
            throw new DomainException(
                "A certificate must carry the digest of what was stored: it is handed to a "
                + "customer as proof of cover, so it has to be provable to be what we received.");
        if (sizeBytes <= 0)
            throw new DomainException("An empty certificate must not be recorded as one.");
        ArgumentNullException.ThrowIfNull(clock);

        return new PolicyCertificate
        {
            Id = id ?? Guid.NewGuid(),
            TenantId = tenantId,
            PolicyId = policyId,
            FileName = string.IsNullOrWhiteSpace(fileName) ? "attestation.pdf" : fileName.Trim(),
            ContentType = string.IsNullOrWhiteSpace(contentType)
                ? "application/octet-stream"
                : contentType.Trim(),
            SizeBytes = sizeBytes,
            Sha256 = sha256.Trim().ToLowerInvariant(),
            StorageRef = storageRef.Trim(),
            InsurerCertificateRef = insurerCertificateRef?.Trim(),
            ValidFrom = validFrom,
            ValidTo = validTo,
            FetchedAt = clock.GetUtcNow(),
        };
    }
}
