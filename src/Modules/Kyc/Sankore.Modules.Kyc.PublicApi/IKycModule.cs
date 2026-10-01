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

    /// <summary>
    /// The operation ceilings in force for a customer (KYC-B-06).
    ///
    /// A customer on the SIMPLIFIED tier may operate, but capped: a balance ceiling and a flow
    /// ceiling over a rolling window. A FULL customer is uncapped — <see cref="KycLimits.IsCapped"/>
    /// is false and the amounts are meaningless. An EXPIRED file falls back to the simplified
    /// ceilings rather than blocking the customer outright.
    ///
    /// Returns <c>null</c> when the customer has no KYC file at all, which a caller must treat as
    /// "not allowed to operate" rather than "no limits".
    /// </summary>
    Task<KycLimits?> GetLimitsAsync(Guid tenantId, Guid customerEntityId, CancellationToken ct);

    /// <summary>
    /// What the customer has already consumed of the flow ceiling over the current window.
    ///
    /// <para>
    /// NOT IMPLEMENTED, and it cannot be: the flow is the sum of deposits and withdrawals across
    /// every account of the customer, and no module in this solution owns an account, a balance or
    /// a transaction. M03 (Épargne) and M07 (Mobile Money) do not exist. The contract is declared
    /// so those modules can be written against it; the implementation answers
    /// <see cref="KycFlowUsage.Unknown"/>, and a caller that cannot measure the flow must refuse
    /// the operation rather than assume it fits.
    /// </para>
    /// </summary>
    Task<KycFlowUsage> GetFlowUsageAsync(Guid tenantId, Guid customerEntityId, CancellationToken ct);
}

/// <param name="IsCapped">False for a full KYC: the amounts below carry no meaning.</param>
/// <param name="MaxBalance">Ceiling on the balance AFTER an operation, in the tenant's currency.</param>
/// <param name="MaxFlow">Ceiling on deposits plus withdrawals over <paramref name="WindowDays"/>.</param>
/// <param name="AlertPct">Percentage of the flow ceiling at which the agent is warned.</param>
public sealed record KycLimits(
    string Tier,
    bool IsCapped,
    decimal MaxBalance,
    decimal MaxFlow,
    int WindowDays,
    int AlertPct);

/// <param name="Known">
/// False when the flow could not be measured. A caller must then refuse the operation: an
/// unmeasured flow is not a zero flow, and treating it as one is how a capped customer ends up
/// uncapped.
/// </param>
public sealed record KycFlowUsage(bool Known, decimal Consumed, DateTimeOffset WindowStart)
{
    public static KycFlowUsage Unknown { get; } = new(false, 0m, DateTimeOffset.MinValue);
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
