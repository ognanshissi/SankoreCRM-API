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
    /// What this adapter can do, and in which mode. Read from the adapter and not from a central
    /// table because the answer depends on the installation: INT-31 computes it from the
    /// Amplitude version configured on the connection.
    /// </summary>
    IntegrationCapabilities Capabilities { get; }

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
