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
    string? ConsentEvidenceRef);

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
