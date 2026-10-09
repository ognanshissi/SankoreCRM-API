namespace Sankore.Modules.Integration.Ports;

using Sankore.Modules.Integration.PublicApi;

/// <summary>Product catalogue, pricing and eligibility at the insurer (ASS-02, ASS-03).</summary>
public interface IInsuranceProductPort
{
    /// <summary>
    /// The insurer's own catalogue, when it exposes one. A tenant that configures its products by
    /// hand needs no such capability, which is why this is declared and not assumed.
    /// </summary>
    Task<IntegrationResult<IReadOnlyList<string>>> GetProductCodesAsync(CancellationToken ct);

    Task<IntegrationResult<InsuranceQuote>> PriceAsync(
        string insurerProductCode, Guid crmCustomerId, decimal? insuredAmount, CancellationToken ct);

    Task<IntegrationResult<InsuranceEligibility>> CheckEligibilityAsync(
        string insurerProductCode, Guid crmCustomerId, CancellationToken ct);
}

/// <summary>Policy lifecycle at the insurer (ASS-04, ASS-07).</summary>
public interface IInsurancePolicyPort
{
    Task<IntegrationResult<ExternalId>> SubscribeAsync(
        InsurancePolicyPayload payload, IdempotencyKey key, CancellationToken ct);

    Task<IntegrationResult<IReadOnlyList<InsurancePolicy>>> GetPoliciesAsync(
        ExternalId customerId, CancellationToken ct);

    Task<IntegrationResult<InsurancePolicy>> GetPolicyAsync(
        ExternalId policyId, CancellationToken ct);

    /// <summary>
    /// The certificate, as the insurer produces it. Returned as bytes and stored encrypted: it
    /// carries the insured's identity and is handed to the customer, so it is evidence.
    /// </summary>
    Task<IntegrationResult<InsuranceCertificate>> GetCertificateAsync(
        ExternalId policyId, CancellationToken ct);

    Task<IntegrationResult> CancelAsync(
        ExternalId policyId, string reason, IdempotencyKey key, CancellationToken ct);
}

/// <summary>Claim declaration and follow-up (ASS-09).</summary>
public interface IInsuranceClaimPort
{
    Task<IntegrationResult<ExternalId>> DeclareAsync(
        InsuranceClaimPayload payload, IdempotencyKey key, CancellationToken ct);

    Task<IntegrationResult<IReadOnlyList<InsuranceClaim>>> GetClaimsAsync(
        ExternalId policyId, CancellationToken ct);

    Task<IntegrationResult<InsuranceClaim>> GetClaimAsync(
        ExternalId claimId, CancellationToken ct);

    /// <summary>Adds a document the insurer asked for after the declaration.</summary>
    Task<IntegrationResult> AddDocumentAsync(
        ExternalId claimId, string storageRef, IdempotencyKey key, CancellationToken ct);
}
