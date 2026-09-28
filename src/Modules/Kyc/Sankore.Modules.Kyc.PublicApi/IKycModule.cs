namespace Sankore.Modules.Kyc.PublicApi;

using Sankore.Shared.Kernel;

/// <summary>
/// Public contract exposed by the KYC module (M02).
/// Other modules reference only this interface — never the main assembly.
/// The KYC module consumes <see cref="KycRequestedIntegrationEvent"/>
/// asynchronously via the outbox/MassTransit.
/// </summary>
public interface IKycModule
{
    /// <summary>
    /// Returns the current KYC status for a customer entity.
    /// </summary>
    Task<KycStatus?> GetStatusAsync(Guid tenantId, Guid customerEntityId, CancellationToken ct);

    /// <summary>
    /// True when the compliance side has no remaining obligation to keep this
    /// customer's KYC evidence, i.e. the record may be anonymized.
    ///
    /// Deliberately fail-closed: an implementation that does not know (module not
    /// deployed yet, file still open, pending investigation or legal hold) returns
    /// <c>false</c>, and the caller reports <c>KYC_RETENTION_NOT_CLEARED</c>
    /// rather than destroying data it might still owe to a regulator.
    /// </summary>
    Task<bool> IsRetentionClearedAsync(Guid tenantId, Guid customerEntityId, CancellationToken ct);
}

public enum KycStatus
{
    NotStarted,
    Pending,
    InProgress,
    Approved,
    Rejected,
    Expired
}

/// <summary>
/// Published by the Leads module when a lead is converted and KYC must be initiated (US-M13-172).
/// Consumed asynchronously by the KYC module — no synchronous call, no physical FK.
/// </summary>
public sealed record KycRequestedIntegrationEvent(
    Guid TenantId,
    Guid CustomerEntityId,
    Guid LeadId,
    string FullName,
    string PhoneNumber,
    string? Email,
    string? NationalId,
    DateOnly? DateOfBirth,
    Guid RequestedBy) : IntegrationEventBase;
