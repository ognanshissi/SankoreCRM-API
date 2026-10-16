namespace Sankore.Modules.Integration.PublicApi;

using Sankore.Shared.Kernel;

/// <summary>
/// The module's whole public surface: three gateways, one per family of back-office (INT-02,
/// ASS-02, TEL-B-02).
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

    /// <summary>
    /// Telephony (TEL-B-02). Never null, like its siblings: a tenant with no telephony connection
    /// gets a gateway that answers "no active connection", rather than a null property every
    /// caller would have to test before using.
    /// </summary>
    ITelephonyGateway Telephony { get; }
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

    /// <summary>
    /// Queues one attempt at collecting one premium INSTALMENT (ASS-08).
    ///
    /// <para>
    /// Separate from <see cref="RequestPremiumDebitAsync"/>, which collects the premium of a
    /// SUBSCRIPTION and is idempotent on it so a retried request cannot debit twice. An instalment
    /// is retried on purpose, on the product's own schedule, so its key carries the attempt — each
    /// one is a genuine second ask of the core banking system.
    /// </para>
    /// </summary>
    Task<IntegrationCommandId> RequestInstalmentDebitAsync(
        InstalmentDebitRequest request, CancellationToken ct);

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

    /// <summary>
    /// Queues one <i>pièce de sinistre</i> for the insurer (ASS-09).
    ///
    /// <para>
    /// The connection is derived from the dossier's own insurer reference rather than passed, the
    /// same way <see cref="RequestClaimDeclarationAsync"/> derives it: a caller holding a document
    /// id has no business choosing which insurer receives it.
    /// </para>
    /// </summary>
    Task<IntegrationCommandId> RequestClaimDocumentAsync(
        ClaimDocumentPayload payload, CancellationToken ct);

    IntegrationCapabilities GetCapabilities(Guid connectionId);
}

/// <summary>
/// Telephony, as the rest of the platform sees it (TEL-B-02).
///
/// <para>
/// <b>It does not look like the other two gateways, and the difference is the point.</b> A write
/// to a back office is queued as a command and answered later; a telephone call is not. An agent
/// clicking "call" waits for their own handset to ring, so <see cref="RequestClickToCallAsync"/>
/// answers now or fails now — there is no command id to follow, and a retry would make a phone
/// ring twice in somebody's hand.
/// </para>
///
/// <para>
/// No method here takes or returns a telephone NUMBER. The caller names a record and which of its
/// numbers to use; the number is decrypted inside this module and never crosses back out. That is
/// what keeps a number out of a browser, a log and an audit payload at once.
/// </para>
/// </summary>
public interface ITelephonyGateway
{
    /// <summary>Asks the provider to ring this user, then dial one of the record's numbers.</summary>
    Task<ClickToCallResult> RequestClickToCallAsync(
        Guid crmUserId, TelephonyEntityRef target, TelephonyPhoneSlot slot, CancellationToken ct);

    /// <summary>
    /// What is known about one call, for the screen that opens while a telephone is ringing.
    /// Null when the call does not exist for this tenant.
    /// </summary>
    Task<TelephonyCallContext?> GetCallContextAsync(Guid callId, CancellationToken ct);

    /// <summary>
    /// Attaches a call to a record, or corrects an attachment. The reason is mandatory and kept:
    /// a conversation filed against the wrong person is something a later reader must be able to
    /// understand.
    /// </summary>
    Task<Result> LinkCallAsync(
        Guid callId, TelephonyEntityRef target, string reason, CancellationToken ct);

    /// <summary>The calls of one record, newest first.</summary>
    Task<IReadOnlyList<TelephonyCallSummary>> GetCallsForEntityAsync(
        TelephonyEntityRef entity, int page, int pageSize, CancellationToken ct);

    /// <summary>
    /// What the tenant's provider can do. <c>None</c> when no telephony connection is active,
    /// which is how a caller tells "switched off" from "cannot do that".
    /// </summary>
    IntegrationCapabilities GetCapabilities();
}

/// <summary>
/// Which record a call is about. An opaque pair — the type as a string, the id as a Guid — so no
/// module has to reference another's domain to name one.
/// </summary>
public sealed record TelephonyEntityRef(string Type, Guid Id);

/// <summary>
/// Which of a record's numbers to dial. <b>A slot and not a number</b>: the front names the slot,
/// the server decrypts the number. A contract taking a number would be a contract a browser can
/// be made to post somebody else's number to.
/// </summary>
public enum TelephonyPhoneSlot
{
    Primary,
    Secondary,
}

/// <param name="RequestId">
/// The click-to-call request, for the screen to follow until the agent's handset rings.
/// </param>
/// <param name="ErrorCode">
/// Null on success. A stable code — the agent's screen turns it into a sentence, and the set is
/// closed so a new refusal cannot read as a generic failure.
/// </param>
public sealed record ClickToCallResult(Guid? RequestId, string? ErrorCode)
{
    public bool IsSuccess => ErrorCode is null;

    public static ClickToCallResult Ok(Guid requestId) => new(requestId, null);

    public static ClickToCallResult Refused(string errorCode) => new(null, errorCode);
}

/// <param name="Candidates">
/// Who the remote number might be. Several is an ordinary answer — a family handset, a shop line
/// — and the screen asks the agent rather than guessing.
/// </param>
public sealed record TelephonyCallContext(
    Guid CallId,
    string Direction,
    string Status,
    string MatchStatus,
    IReadOnlyList<TelephonyCandidate> Candidates,
    DateTimeOffset StartedAt);

/// <param name="OutOfPerimeter">
/// True when the record exists but belongs to another agency. The name is then withheld and the
/// screen says only that the caller is known elsewhere — the perimeter hides the record, not the
/// fact that the number is known, which is what stops an agent searching for nothing.
/// </param>
public sealed record TelephonyCandidate(
    string EntityType,
    Guid EntityId,
    string? DisplayName,
    bool OutOfPerimeter);

public sealed record TelephonyCallSummary(
    Guid CallId,
    string Direction,
    string Status,
    DateTimeOffset StartedAt,
    int? TalkSeconds,
    string? OutcomeCode,
    Guid? CrmUserId);

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

/// <param name="Attempt">
/// Which attempt this is, starting at 1. Part of the idempotency key — see
/// <see cref="ICoreBankingGateway.RequestInstalmentDebitAsync"/>.
/// </param>
/// <param name="Label">
/// What appears on the customer's statement. Operator-facing, never personal data: it reaches a
/// core banking system this platform does not control.
/// </param>
public sealed record InstalmentDebitRequest(
    Guid InstalmentId,
    string AccountRef,
    decimal Amount,
    string Currency,
    int Attempt,
    string Label);
