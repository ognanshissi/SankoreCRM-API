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

    /// <summary>
    /// Queues the debit of an insurance premium against a customer's account — the FIRST step of
    /// the ASS-05 chain, before anything is asked of the insurer.
    ///
    /// <para>
    /// That order is the whole design: a premium collected and a policy refused can be put right
    /// by a reversal, while a policy issued against a premium that was never collected is an
    /// insurer's receivable against a customer who was never debited. The reversal is the
    /// compensating step, not an afterthought.
    /// </para>
    ///
    /// <para>
    /// Idempotent on the SUBSCRIPTION, so a retried request attaches to the debit already in
    /// flight rather than taking the money twice.
    /// </para>
    /// </summary>
    Task<IntegrationCommandId> RequestPremiumDebitAsync(
        PremiumDebitRequest request, CancellationToken ct);

    /// <summary>
    /// Queues the REVERSAL of a premium debit, keyed on the reference the original debit returned
    /// — the only form a core banking system will safely accept twice.
    ///
    /// <para>
    /// <paramref name="request"/> carries the attempt number because a refused reversal is NOT
    /// retried automatically: the subscription sits in <c>PremiumRefundDue</c> until an operator
    /// re-attempts, and without the attempt in the idempotency key that second attempt would be
    /// swallowed as a replay — leaving a customer un-refunded while the platform reports a refund
    /// as requested.
    /// </para>
    /// </summary>
    Task<IntegrationCommandId> RequestDebitReversalAsync(
        DebitReversalRequest request, CancellationToken ct);

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

/// <param name="SubscriptionId">
/// The subscription this premium belongs to. It is the idempotency anchor AND what the command
/// row carries as its CRM id, so a command found in the rejection queue says what it was for.
/// </param>
/// <param name="AccountRef">The account to debit, as the core banking system knows it.</param>
/// <param name="Label">
/// What appears on the customer's statement. Operator-facing text, never personal data: it reaches
/// a core banking system this platform does not control.
/// </param>
public sealed record PremiumDebitRequest(
    Guid SubscriptionId,
    string AccountRef,
    decimal Amount,
    string Currency,
    string Label);

/// <param name="OriginalReference">
/// The reference the DEBIT returned. A reversal is keyed on it rather than on the account and the
/// amount, because that is the only form a core banking system will accept twice without the risk
/// of refunding twice.
/// </param>
/// <param name="Attempt">
/// Which attempt this is, starting at 1. Part of the idempotency key — see
/// <see cref="ICoreBankingGateway.RequestDebitReversalAsync"/>.
/// </param>
public sealed record DebitReversalRequest(
    Guid SubscriptionId,
    string AccountRef,
    string OriginalReference,
    int Attempt);
