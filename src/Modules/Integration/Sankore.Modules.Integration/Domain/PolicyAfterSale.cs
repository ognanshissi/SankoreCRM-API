namespace Sankore.Modules.Integration.Domain;

using Sankore.Shared.Kernel;

/// <summary>What a client asks of a policy after the sale (insurance lot 3).</summary>
public enum PolicyOperationKind
{
    /// <summary>Renonciation (life, within 30 days): the insurer refunds within 30 days.</summary>
    Renunciation,

    /// <summary>Rachat.</summary>
    Surrender,

    /// <summary>Avance sur contrat.</summary>
    Advance,
}

public enum PolicyOperationStatus
{
    Requested,
    SentToInsurer,
    Accepted,
    Refused,
    Paid,
    Cancelled,
}

/// <summary>
/// A request on a policy — renunciation, surrender, advance — handled by the insurer and followed
/// here (lot 3): the CRM receives and timestamps it, the agent reports what the insurer answered and
/// paid. The renunciation's refund is due within 30 days, and is never recorded paid while the
/// client's KYC is incomplete (LBC/FT): the caller checks it.
/// </summary>
public sealed class PolicyOperation : AggregateRoot
{
    public Guid Id { get; private set; }
    public Guid PolicyId { get; private set; }
    public Guid CrmCustomerId { get; private set; }
    public PolicyOperationKind Kind { get; private set; }
    public PolicyOperationStatus Status { get; private set; }

    /// <summary>When the client's request was received — the timestamp the 30 days run from.</summary>
    public DateTimeOffset RequestedAt { get; private set; }
    public Guid RequestedBy { get; private set; }
    public string? Reason { get; private set; }

    /// <summary>Asked by the client (surrender, advance) or due by the insurer (renunciation refund).</summary>
    public decimal? Amount { get; private set; }
    public string? Currency { get; private set; }

    /// <summary>The renunciation's refund deadline: request + 30 days.</summary>
    public DateOnly? RefundDueBy { get; private set; }

    /// <summary>Where the client wants the money: one of their payment instruments (M01), opaque.</summary>
    public Guid? PayoutInstrumentId { get; private set; }

    public string? InsurerReference { get; private set; }
    public DateTimeOffset? StatusChangedAt { get; private set; }
    public Guid? StatusChangedBy { get; private set; }
    public DateOnly? PaidOn { get; private set; }

    private PolicyOperation() { }

    public static PolicyOperation Request(
        Guid tenantId, Guid policyId, Guid crmCustomerId, PolicyOperationKind kind, decimal? amount,
        string? currency, string? reason, Guid? payoutInstrumentId, Guid by, TimeProvider clock)
    {
        if (kind == PolicyOperationKind.Advance && amount is not > 0m)
            throw new DomainException("An advance is requested with its amount.");

        var now = clock.GetUtcNow();
        return new PolicyOperation
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            PolicyId = policyId,
            CrmCustomerId = crmCustomerId,
            Kind = kind,
            Status = PolicyOperationStatus.Requested,
            RequestedAt = now,
            RequestedBy = by,
            Reason = string.IsNullOrWhiteSpace(reason) ? null : reason.Trim(),
            Amount = amount,
            Currency = currency,
            PayoutInstrumentId = payoutInstrumentId,
            RefundDueBy = kind == PolicyOperationKind.Renunciation
                ? DateOnly.FromDateTime(now.UtcDateTime).AddDays(30)
                : null,
        };
    }

    private static readonly Dictionary<PolicyOperationStatus, PolicyOperationStatus[]> Allowed = new()
    {
        [PolicyOperationStatus.Requested] = [PolicyOperationStatus.SentToInsurer, PolicyOperationStatus.Cancelled],
        [PolicyOperationStatus.SentToInsurer] = [PolicyOperationStatus.Accepted, PolicyOperationStatus.Refused, PolicyOperationStatus.Paid],
        [PolicyOperationStatus.Accepted] = [PolicyOperationStatus.Paid],
        [PolicyOperationStatus.Refused] = [],
        [PolicyOperationStatus.Paid] = [],
        [PolicyOperationStatus.Cancelled] = [],
    };

    public bool IsOpen => Status is PolicyOperationStatus.Requested or PolicyOperationStatus.SentToInsurer or PolicyOperationStatus.Accepted;

    public void MoveTo(
        PolicyOperationStatus next, string? insurerReference, decimal? amount, DateOnly? paidOn, Guid by, TimeProvider clock)
    {
        if (!Allowed[Status].Contains(next))
            throw new DomainException($"A {Status} operation cannot become {next}.");
        if (next == PolicyOperationStatus.Paid && paidOn is null)
            throw new DomainException("A payment is recorded with its date.");

        Status = next;
        if (!string.IsNullOrWhiteSpace(insurerReference)) InsurerReference = insurerReference.Trim();
        if (amount is > 0m) Amount = amount;
        if (paidOn is not null) PaidOn = paidOn;
        StatusChangedAt = clock.GetUtcNow();
        StatusChangedBy = by;
    }
}

/// <summary>The insurer's documents for a policy (lots 3): CG, CP, attestation, avenant, annual information.</summary>
public enum PolicyDocumentKind
{
    GeneralConditions,
    SpecialConditions,
    Certificate,
    Endorsement,

    /// <summary>The art. 75 annual information: produced by the insurer, relayed by the distributor.</summary>
    AnnualStatement,

    Other,
}

/// <summary>
/// A document the insurer issued for a policy, kept in the file store and, when relayed, with the
/// record of its handing to the client (channel, date) — the timestamp a digital sale proves.
/// </summary>
public sealed class PolicyDocument : AggregateRoot
{
    public Guid Id { get; private set; }
    public Guid PolicyId { get; private set; }
    public Guid CrmCustomerId { get; private set; }
    public PolicyDocumentKind Kind { get; private set; }
    public string Title { get; private set; } = string.Empty;
    public string FileReference { get; private set; } = string.Empty;
    public string FileName { get; private set; } = string.Empty;
    public string ContentType { get; private set; } = string.Empty;
    public string Sha256 { get; private set; } = string.Empty;
    public DateTimeOffset UploadedAt { get; private set; }
    public Guid UploadedBy { get; private set; }

    public DateTimeOffset? SentToClientAt { get; private set; }
    public InformationSheetChannel? SentChannel { get; private set; }
    public Guid? SentBy { get; private set; }

    private PolicyDocument() { }

    public static PolicyDocument Upload(
        Guid tenantId, Guid policyId, Guid crmCustomerId, PolicyDocumentKind kind, string title,
        string fileReference, string fileName, string contentType, string sha256, Guid by, TimeProvider clock)
        => new()
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            PolicyId = policyId,
            CrmCustomerId = crmCustomerId,
            Kind = kind,
            Title = string.IsNullOrWhiteSpace(title) ? kind.ToString() : title.Trim(),
            FileReference = fileReference,
            FileName = fileName,
            ContentType = contentType,
            Sha256 = sha256,
            UploadedAt = clock.GetUtcNow(),
            UploadedBy = by,
        };

    public void MarkSentToClient(InformationSheetChannel channel, Guid by, TimeProvider clock)
    {
        SentToClientAt = clock.GetUtcNow();
        SentChannel = channel;
        SentBy = by;
    }
}
