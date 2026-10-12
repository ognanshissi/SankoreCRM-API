namespace Sankore.Modules.Integration.PublicApi;

/// <summary>Where a policy stands, folded from whatever vocabulary the insurer uses (ASS-07).</summary>
public enum PolicyStatus
{
    Pending,
    Issued,
    Suspended,
    Cancelled,
    Expired
}

/// <summary>Claim lifecycle as the agent follows it (ASS-09).</summary>
public enum ClaimStatus
{
    Declared,
    UnderReview,
    DocumentsRequired,
    Accepted,
    Refused,
    Indemnified
}

public enum PremiumPeriodicity
{
    Single,
    Monthly,
    Quarterly,
    SemiAnnual,
    Annual
}

public sealed record InsuranceBeneficiary(
    string FullName,
    string? Relationship,
    decimal SharePercent,
    DateOnly? DateOfBirth);

/// <summary>
/// A subscription request. The medical questionnaire is deliberately NOT here: it is encrypted
/// at the field level and reaches the adapter through its own protected channel, never through a
/// record that could be logged or audited in clear (ASS-12).
/// </summary>
/// <param name="CrmHoldingId">
/// The M01 <c>ProductHolding</c> this subscription is (BNF-B-14/B-16), when the request comes from
/// one. Null for the pre-existing callers, which subscribe a customer to a product with no CRM-side
/// contract record.
///
/// <para>
/// When it is set it becomes the command's <c>CrmId</c> and the idempotency key is built from it
/// and <paramref name="ClauseVersion"/> — so the return path can say WHICH holding the insurer
/// answered about, and an avenant is a second, distinct send rather than a swallowed replay.
/// </para>
/// </param>
/// <param name="ClauseVersion">
/// Which version of the beneficiary clause is being sent. Meaningful only with
/// <paramref name="CrmHoldingId"/>; part of the idempotency key, because the same holding
/// legitimately owes a second write when its clause changes.
/// </param>
public sealed record InsurancePolicyPayload(
    Guid CrmCustomerId,
    Guid CrmProductId,
    string InsurerProductCode,
    DateOnly EffectiveDate,
    decimal PremiumAmount,
    string Currency,
    PremiumPeriodicity Periodicity,
    IReadOnlyList<InsuranceBeneficiary> Beneficiaries,
    string? LinkedLoanReference,
    string? ConsentEvidenceRef,
    Guid? CrmHoldingId = null,
    int ClauseVersion = 1);

public sealed record InsurancePolicy(
    ExternalId PolicyId,
    string PolicyNumber,
    Guid CrmCustomerId,
    string InsurerProductCode,
    PolicyStatus Status,
    DateOnly EffectiveDate,
    DateOnly? ExpiryDate,
    decimal PremiumAmount,
    string Currency,
    PremiumPeriodicity Periodicity,
    DateOnly? NextDueDate);

public sealed record InsuranceClaimPayload(
    ExternalId PolicyId,
    DateOnly OccurredOn,
    string Nature,
    string Description,
    IReadOnlyList<string> DocumentStorageRefs);

/// <summary>
/// One <i>pièce de sinistre</i> on its way to the insurer (ASS-09).
/// </summary>
/// <param name="StorageRef">
/// The reference in THIS platform's encrypted store, which is what
/// <c>IInsuranceClaimPort.AddDocumentAsync</c> takes. An adapter that has to push bytes reads them
/// through the store; one that passes a reference on to an insurer's own document service passes
/// this. Either way the bytes are never in the command payload: a <c>CommandType</c> row is
/// replayed, logged and reconciled, and putting a photograph of a wrecked vehicle in one would put
/// it through every one of those.
/// </param>
/// <param name="Sha256">
/// Digest of the plaintext, so an insurer that computes its own can confirm it received what was
/// sent — and so a reconciliation can tell a re-send from a different file.
/// </param>
public sealed record ClaimDocumentPayload(
    string ExternalClaimId,
    Guid ClaimDocumentId,
    string StorageRef,
    string DocumentKind,
    string FileName,
    string ContentType,
    long SizeBytes,
    string Sha256);

public sealed record InsuranceClaim(
    ExternalId ClaimId,
    string ClaimNumber,
    ExternalId PolicyId,
    ClaimStatus Status,
    DateOnly OccurredOn,
    DateTimeOffset DeclaredAt,
    decimal? IndemnityAmount,
    string? MissingDocuments);

/// <summary>A price the insurer computed, when the capability exists (ASS-03).</summary>
public sealed record InsuranceQuote(
    string InsurerProductCode,
    decimal PremiumAmount,
    string Currency,
    PremiumPeriodicity Periodicity,
    DateTimeOffset QuotedAt);

/// <summary>
/// Eligibility verdict. A refusal carries its reasons because the agent has to explain it to the
/// customer at the counter — "not eligible" alone sends them away without knowing why (ASS-04).
/// </summary>
public sealed record InsuranceEligibility(bool IsEligible, IReadOnlyList<string> Reasons);

/// <summary>A certificate fetched from the insurer, stored encrypted (ASS-07).</summary>
public sealed record InsuranceCertificate(
    ExternalId PolicyId,
    string ContentType,
    byte[] Content,
    string FileName);
