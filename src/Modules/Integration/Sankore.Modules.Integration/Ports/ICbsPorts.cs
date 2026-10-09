namespace Sankore.Modules.Integration.Ports;

using Sankore.Modules.Integration.Domain;
using Sankore.Modules.Integration.PublicApi;

/// <summary>
/// What every adapter must be able to answer about itself (INT-02).
///
/// <para>
/// Public, and in the module rather than the PublicApi: the adapters live in their own
/// assemblies (<c>Sankore.Modules.Integration.Adapters.*</c>) and must implement these, while no
/// OTHER module may reference an adapter project at all — a rule an architecture test enforces.
/// </para>
/// </summary>
public interface ICbsAdapter
{
    IntegrationKind Kind { get; }

    /// <summary>
    /// What this adapter can do <b>for one connection</b>, and in which mode. Read from the
    /// adapter and not from a central table because the answer depends on the installation:
    /// INT-31 computes it from the Amplitude version configured on the connection, and ORASS from
    /// the carrier its settings and mode resolve to.
    ///
    /// <para>
    /// <b>It takes its subject, and that is the whole point.</b> This was a parameterless property
    /// until L8, and the omission was not cosmetic. An adapter is resolved by the connection's
    /// KIND, so a parameterless property forced any adapter whose matrix depends on the row to
    /// re-discover "the tenant's connection" by kind and answer for whichever row it happened to
    /// pick — in practice the oldest active one. For core banking that is invisible: a tenant has
    /// at most one active core-banking connection, enforced by
    /// <c>ux_integration_connection_active_core_banking</c>. For INSURANCE it is not, and
    /// deliberately so: ASS-01 places no such limit on the insurance family because an IMF
    /// distributing ORASS IARD and ORASS Vie legitimately holds two ACTIVE connections of the same
    /// kind whose matrices can differ. <c>IInsuranceGateway.GetCapabilities(connectionId)</c> —
    /// which exists per connection precisely because « each insurer has its own adapter and its own
    /// supported operations » — then returned the same matrix for both, and a product of the other
    /// connection was offered a live button that failed at the counter.
    /// </para>
    ///
    /// <para>
    /// Same subject and same type as <see cref="CheckHealthAsync"/>, so the two members of this
    /// contract that depend on a row ask for it the same way. The connection carries its own
    /// <c>Settings</c> and <c>Mode</c>, so an implementation needs no database read to answer:
    /// the matrix is a pure function of the row, which is what every <c>…CapabilityMatrix.For</c>
    /// in the adapter assemblies already was.
    /// </para>
    ///
    /// <para>
    /// <b>Inside this module there is exactly one caller</b> — <c>ResolvedAdapter.Capabilities</c>,
    /// which is the type that pairs a connection with the adapter serving it. Everything else
    /// reads that. One pairing site is what stops the connection being dropped on the floor again.
    /// </para>
    /// </summary>
    IntegrationCapabilities CapabilitiesFor(IntegrationConnection connection);

    /// <summary>
    /// Reaches the external system and says whether it answered. Called by the health-check
    /// endpoint, and required to have passed before a connection may be activated (INT-03).
    /// </summary>
    Task<IntegrationHealth> CheckHealthAsync(IntegrationConnection connection, CancellationToken ct);
}

/// <summary>
/// Customer operations of a core banking system (INT-12).
///
/// <para>
/// Every write takes an <see cref="IdempotencyKey"/>. It is not decoration: a timeout leaves the
/// adapter unable to tell "not created" from "created, answer lost", and the key is what lets
/// the retry be a no-op on the CBS side rather than a second customer.
/// </para>
/// </summary>
public interface ICbsCustomerPort
{
    Task<IntegrationResult<ExternalId>> CreateCustomerAsync(
        CbsCustomerPayload payload, IdempotencyKey key, CancellationToken ct);

    Task<IntegrationResult> UpdateCustomerAsync(
        ExternalId id, CbsCustomerPayload payload, IdempotencyKey key, CancellationToken ct);

    Task<IntegrationResult> SetKycLevelAsync(
        ExternalId id, KycLevel level, IdempotencyKey key, CancellationToken ct);
}

/// <summary>Account operations (INT-13).</summary>
public interface ICbsAccountPort
{
    Task<IntegrationResult<ExternalId>> OpenAccountAsync(
        ExternalId customerId, string productCode, IdempotencyKey key, CancellationToken ct);

    Task<IntegrationResult<IReadOnlyList<CbsAccount>>> GetAccountsAsync(
        ExternalId customerId, CancellationToken ct);

    Task<IntegrationResult<CbsBalance>> GetBalanceAsync(ExternalId accountId, CancellationToken ct);

    /// <summary>
    /// Debits a premium (ASS-05). Added to the CBS port rather than to an insurance one on
    /// purpose: the money moves in the core banking system, and the insurer never touches it.
    /// </summary>
    Task<IntegrationResult<CbsDebitReceipt>> DebitAccountAsync(
        ExternalId accountId, decimal amount, string currency, string label,
        IdempotencyKey key, CancellationToken ct);

    /// <summary>
    /// Reverses a debit, by the reference the original returned. Exists because the orchestration
    /// debits BEFORE the insurer accepts: a refused subscription must give the money back, and a
    /// reversal keyed on the original reference is the only form a CBS will accept twice safely.
    /// </summary>
    Task<IntegrationResult<CbsDebitReceipt>> ReverseDebitAsync(
        ExternalId accountId, string originalReference, IdempotencyKey key, CancellationToken ct);
}

/// <summary>Transaction history and monthly flow (INT-13, INT-22).</summary>
public interface ICbsTransactionPort
{
    Task<IntegrationResult<CbsPage<CbsTransaction>>> GetTransactionsAsync(
        ExternalId accountId, DateOnly from, DateOnly to, string? cursor, CancellationToken ct);

    Task<IntegrationResult<CbsMonthlyFlow>> GetMonthlyFlowAsync(
        ExternalId customerId, YearMonth month, CancellationToken ct);
}

/// <summary>Loan operations.</summary>
public interface ICbsLoanPort
{
    Task<IntegrationResult<ExternalId>> SubmitLoanApplicationAsync(
        CbsLoanApplicationPayload payload, IdempotencyKey key, CancellationToken ct);

    Task<IntegrationResult<IReadOnlyList<CbsLoan>>> GetLoansAsync(
        ExternalId customerId, CancellationToken ct);
}

/// <summary>
/// Reading a customer's KYC tier back from the external system — <b>optional</b>, and separate
/// from <see cref="ICbsCustomerPort"/> on purpose.
///
/// <para>
/// It is its own interface rather than a member of the customer port because adapters differ on
/// it in kind, not in quality: Transact exposes a party's KYC status as a field one can query,
/// while a batch-file CBS accepts the write and offers no query at all. Putting the getter on
/// <see cref="ICbsCustomerPort"/> would force every adapter to implement a method half of them
/// can only answer "not supported" to — and would have broken every existing adapter the day it
/// was added.
/// </para>
///
/// <para>
/// INT-21 needs it for the divergence of criterion 4 to mean what the specification intends.
/// Without it the snapshot can only infer the CBS tier from the writes the CBS acknowledged,
/// which catches "we pushed and it failed" — already visible in the command queue — but is blind
/// to the case compliance actually cares about: a tier changed by an officer inside the CBS,
/// which nothing in this platform would otherwise ever learn.
/// </para>
///
/// <para>
/// An adapter that implements this MUST declare
/// <see cref="IntegrationCapability.ReadKycLevel"/>; one that does not is left to the inference,
/// and the snapshot records the difference rather than pretending the two are the same.
/// </para>
/// </summary>
public interface ICbsKycLevelPort
{
    /// <summary>
    /// The tier the external system holds, or <c>null</c> when it holds none for this customer.
    /// Null is a state and not an error: a customer the CBS knows but has never rated is ordinary,
    /// and reporting it as a failure would fill the compliance queue with rows whose only content
    /// is that nobody has rated anybody.
    /// </summary>
    Task<IntegrationResult<KycLevel?>> GetKycLevelAsync(ExternalId customerId, CancellationToken ct);
}
