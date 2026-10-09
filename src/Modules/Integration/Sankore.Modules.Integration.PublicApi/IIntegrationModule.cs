namespace Sankore.Modules.Integration.PublicApi;

/// <summary>
/// The module's whole public surface: two gateways, one per family of back-office (INT-02).
///
/// <para>
/// A consumer module (M01, M02, M13, and the insurance screens) depends on THIS and never on an
/// adapter. The point is not tidiness — it is that the IMF's CBS is chosen by the IMF: the same
/// SANKORE deployment serves a Temenos tenant and a batch-file tenant, and no caller may contain
/// a branch on which.
/// </para>
/// </summary>
public interface IIntegrationModule
{
    ICoreBankingGateway CoreBanking { get; }

    IInsuranceGateway Insurance { get; }
}

/// <summary>
/// Core banking, as the rest of the platform sees it.
///
/// <para>
/// The three <c>Request…Async</c> methods return an <see cref="IntegrationCommandId"/> and not a
/// result: they enqueue, inside the caller's own transaction, and the dispatcher delivers
/// out of band. A client validated at 23:00 while the CBS is down must still be created when it
/// comes back, and the agent must not be made to wait for it (INT-05).
/// </para>
///
/// <para>
/// The two read methods answer now, from the snapshot or from the CBS, and may legitimately
/// return <c>null</c>: a customer the CBS has never heard of is an ordinary state, not an error.
/// </para>
/// </summary>
public interface ICoreBankingGateway
{
    Task<IntegrationCommandId> RequestCustomerCreationAsync(Guid crmCustomerId, CancellationToken ct);

    Task<IntegrationCommandId> RequestKycLevelUpdateAsync(Guid crmCustomerId, KycLevel level, CancellationToken ct);

    Task<IntegrationCommandId> RequestAccountOpeningAsync(Guid crmCustomerId, string productCode, CancellationToken ct);

    Task<CbsCustomerSnapshot?> GetCustomerSnapshotAsync(Guid crmCustomerId, CancellationToken ct);

    Task<CbsBalance?> GetLiveBalanceAsync(Guid crmCustomerId, string accountRef, CancellationToken ct);

    /// <summary>Capabilities of the current tenant's active core-banking connection.</summary>
    IntegrationCapabilities GetCapabilities();
}

/// <summary>
/// Insurance, as the rest of the platform sees it (ASS-02).
///
/// <para>
/// Same shape as <see cref="ICoreBankingGateway"/> on purpose — writes are queued commands,
/// reads answer from a read model — but <see cref="GetCapabilities"/> takes a connection id:
/// a tenant may distribute for several insurers at once, each with its own adapter and its own
/// supported operations.
/// </para>
/// </summary>
public interface IInsuranceGateway
{
    Task<IntegrationCommandId> RequestPolicySubscriptionAsync(
        InsurancePolicyPayload payload, CancellationToken ct);

    Task<IReadOnlyList<InsurancePolicy>> GetPoliciesAsync(Guid crmCustomerId, CancellationToken ct);

    Task<IntegrationCommandId> RequestClaimDeclarationAsync(
        InsuranceClaimPayload payload, CancellationToken ct);

    Task<IReadOnlyList<InsuranceClaim>> GetClaimsAsync(Guid crmCustomerId, CancellationToken ct);

    IntegrationCapabilities GetCapabilities(Guid connectionId);
}
