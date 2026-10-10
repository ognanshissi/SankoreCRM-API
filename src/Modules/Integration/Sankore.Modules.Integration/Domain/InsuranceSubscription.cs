namespace Sankore.Modules.Integration.Domain;

using Sankore.Modules.Integration.PublicApi;
using Sankore.Shared.Kernel;

/// <summary>
/// The durable state of one subscription attempt, across the three commands ASS-05 splits it into
/// (ASS-04, ASS-05).
///
/// <para>
/// <b>Why it exists.</b> ASS-05 orders the steps — debit the premium, then subscribe at the
/// insurer, then reverse the debit if the insurer refuses — and requires each to be a distinct
/// <c>integration_command</c> chained by event, with « l'état global de la souscription »
/// readable. No command row can hold that state: each one knows only its own outcome, and the
/// same <c>Rejected</c> status means "the insurer said no" on one row and "the customer was not
/// refunded" on another. In-memory correlation cannot hold it either — « consultable » means a
/// screen reads it, and a process restart must not lose the fact that money has moved.
/// </para>
///
/// <para>
/// <b>Why it is not the policy.</b> <see cref="PolicyRecord"/> is a read model of what the
/// insurer holds, and ASS-07 synchronises it for policies SANKORE never subscribed — sold at the
/// insurer's own counter, picked up by the sync. Most policy rows would therefore have no saga,
/// and a saga that fails at the debit produces no policy at all. Two lifecycles, two identities
/// (this one is keyed by customer + product + effective date, a policy by the insurer's number),
/// two tables.
/// </para>
///
/// <para>
/// <b>Why the CBS reference is a column.</b> <c>ICbsAccountPort.ReverseDebitAsync</c> takes the
/// reference the original debit returned — "a reversal keyed on the original reference is the only
/// form a CBS will accept twice safely" — and ASS-10's statement needs the same reference and the
/// same amount to justify a premium. If either lived only inside a command's encrypted payload or
/// its <c>ExternalResponseRef</c>, the refund and the monthly justification would both become
/// archaeology through the rejection queue.
/// </para>
/// </summary>
public sealed class InsuranceSubscription : AggregateRoot
{
    public Guid Id { get; private set; }

    /// <summary>The insurer. Intra-schema FK.</summary>
    public Guid ConnectionId { get; private set; }

    /// <summary>
    /// The customer, as an OPAQUE reference to M01. No foreign key: the customer lives in another
    /// schema and this module never crosses one with a physical key.
    /// </summary>
    public Guid CrmCustomerId { get; private set; }

    /// <summary>The catalogue entry subscribed. Intra-schema FK to <c>ins_product</c>.</summary>
    public Guid ProductId { get; private set; }

    /// <summary>
    /// The third component of ASS-04's idempotency. Part of
    /// <c>ux_ins_subscription_idempotency</c>, so a retried request attaches to this row instead
    /// of starting a second debit.
    /// </summary>
    public DateOnly EffectiveDate { get; private set; }

    public SubscriptionStatus Status { get; private set; }

    // ── The premium, and its CBS footprint ──────────────────────────────────

    /// <summary>
    /// What was asked of the CBS. Snapshotted at creation: a catalogue price edited next month
    /// must not change what this subscription says it debited.
    /// </summary>
    public decimal PremiumAmount { get; private set; }

    public string Currency { get; private set; } = string.Empty;

    /// <summary>
    /// The account debited, as the CBS knows it. In clear and bounded, like every other external
    /// identifier in this schema (<c>integration_reference.external_id</c>): it is a reference,
    /// not a credential, and the statement and the reversal both read it.
    /// </summary>
    public string? CbsAccountRef { get; private set; }

    /// <summary>
    /// The reference the CBS returned for the debit. <b>The only input a reversal can be keyed
    /// on</b>, and the reference ASS-10's statement prints.
    /// </summary>
    public string? CbsDebitReference { get; private set; }

    public DateTimeOffset? DebitedAt { get; private set; }

    /// <summary>The reference of the reversal itself, when one was accepted.</summary>
    public string? CbsReversalReference { get; private set; }

    public DateTimeOffset? ReversedAt { get; private set; }

    /// <summary>
    /// How many reversals have been attempted. Stored because
    /// <see cref="SubscriptionStatus.PremiumRefundDue"/> is not retried automatically: an operator
    /// re-attempting needs to know whether the CBS has already refused once.
    /// </summary>
    public int ReversalAttempts { get; private set; }

    // ── The three commands of the chain ─────────────────────────────────────
    //
    // Plain Guids with no foreign key, each with its own filtered index. The event consumer that
    // advances this saga arrives holding a COMMAND id and nothing else, so looking the saga up by
    // it is the hot path of the whole chain. No FK, for the reason integration_command itself
    // keeps none to its connection in the other direction: a command may be purged by a retention
    // job long after the subscription is settled, and a cascade would then take the financial
    // record with it. Every reader degrades to "no longer available".

    public Guid? DebitCommandId { get; private set; }

    public Guid? SubscribeCommandId { get; private set; }

    public Guid? ReversalCommandId { get; private set; }

    // ── The outcome ─────────────────────────────────────────────────────────

    /// <summary>The policy this produced, once the insurer issued it. Intra-schema FK.</summary>
    public Guid? PolicyId { get; private set; }

    /// <summary>
    /// The contract's reference at the insurer (ASS-04, last criterion). Kept here as well as on
    /// the policy: a subscription is the audit of what we sent and got back, and it must stay
    /// readable if the policy read model is later rebuilt from a synchronisation.
    /// </summary>
    public string? InsurerPolicyReference { get; private set; }

    /// <summary>
    /// Beneficiaries, encrypted (AES-256-GCM under this module's key). Names, relationships and
    /// dates of birth of people who are not the customer — PII about third parties the platform
    /// has no other record of. <c>text</c> with no cap: <c>v1:nonce:tag:ciphertext</c> grows with
    /// the number of beneficiaries and a bounded column would truncate it into garbage.
    /// </summary>
    public string? BeneficiariesEncrypted { get; private set; }

    /// <summary>Error code of the step that stopped the chain. Never a payload value.</summary>
    public string? FailureCode { get; private set; }

    public string? FailureDetail { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }
    public Guid CreatedBy { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }

    public uint Version { get; private set; }

    private InsuranceSubscription() { }

    public static InsuranceSubscription Create(
        Guid tenantId,
        Guid connectionId,
        Guid crmCustomerId,
        Guid productId,
        DateOnly effectiveDate,
        decimal premiumAmount,
        string currency,
        Guid createdBy,
        TimeProvider clock,
        string? cbsAccountRef = null,
        string? beneficiariesEncrypted = null,
        Guid? id = null)
    {
        if (tenantId == Guid.Empty) throw new DomainException("TenantId is required.");
        if (connectionId == Guid.Empty) throw new DomainException("ConnectionId is required.");
        if (crmCustomerId == Guid.Empty) throw new DomainException("CrmCustomerId is required.");
        if (productId == Guid.Empty) throw new DomainException("ProductId is required.");
        if (premiumAmount <= 0m)
            throw new DomainException("A subscription needs a positive premium to debit.");
        if (string.IsNullOrWhiteSpace(currency))
            throw new DomainException("A premium amount requires its currency.");
        ArgumentNullException.ThrowIfNull(clock);

        var now = clock.GetUtcNow();

        return new InsuranceSubscription
        {
            Id = id ?? Guid.NewGuid(),
            TenantId = tenantId,
            ConnectionId = connectionId,
            CrmCustomerId = crmCustomerId,
            ProductId = productId,
            EffectiveDate = effectiveDate,
            PremiumAmount = premiumAmount,
            Currency = currency.Trim().ToUpperInvariant(),
            CbsAccountRef = cbsAccountRef?.Trim(),
            BeneficiariesEncrypted = beneficiariesEncrypted,
            Status = SubscriptionStatus.PendingDebit,
            CreatedAt = now,
            CreatedBy = createdBy,
            UpdatedAt = now,
        };
    }

    /// <summary>
    /// Records which command carries the premium debit. Separate from
    /// <see cref="Create"/> because the command is enqueued by the facade, in the same
    /// transaction, and its id only exists once that call has returned.
    /// </summary>
    public void AttachDebitCommand(Guid commandId, TimeProvider clock)
    {
        Require(SubscriptionStatus.PendingDebit, nameof(AttachDebitCommand));

        DebitCommandId = commandId;
        Touch(clock);
    }

    /// <summary>The CBS debited. The money has moved, and the reference is what can undo it.</summary>
    public void MarkDebited(string cbsDebitReference, TimeProvider clock)
    {
        if (string.IsNullOrWhiteSpace(cbsDebitReference))
            throw new DomainException(
                "A debit with no CBS reference cannot be reversed and cannot be justified on a "
                + "statement; recording one would lose the only handle on the customer's money.");

        Require(SubscriptionStatus.PendingDebit, nameof(MarkDebited));

        CbsDebitReference = cbsDebitReference.Trim();
        DebitedAt = clock.GetUtcNow();
        Status = SubscriptionStatus.Debited;
        Touch(clock);
    }

    /// <summary>
    /// The debit was refused. Terminal: nothing was sent to the insurer, no money moved
    /// (ASS-05, criterion 3).
    /// </summary>
    public void MarkDebitFailed(string code, string? detail, TimeProvider clock)
    {
        Require(SubscriptionStatus.PendingDebit, nameof(MarkDebitFailed));

        Fail(code, detail);
        Status = SubscriptionStatus.DebitFailed;
        Touch(clock);
    }

    public void AttachSubscribeCommand(Guid commandId, TimeProvider clock)
    {
        Require(SubscriptionStatus.Debited, nameof(AttachSubscribeCommand));

        SubscribeCommandId = commandId;
        Touch(clock);
    }

    /// <summary>
    /// The insurer issued the policy. Terminal and successful; the caller publishes
    /// <c>PolicyIssuedEvent</c> (ASS-04, last criterion).
    /// </summary>
    public void MarkIssued(Guid policyId, string insurerPolicyReference, TimeProvider clock)
    {
        if (string.IsNullOrWhiteSpace(insurerPolicyReference))
            throw new DomainException("An issued policy must carry the insurer's own reference.");

        Require(SubscriptionStatus.Debited, nameof(MarkIssued));

        PolicyId = policyId;
        InsurerPolicyReference = insurerPolicyReference.Trim();
        Status = SubscriptionStatus.Issued;
        Touch(clock);
    }

    /// <summary>The insurer refused after the premium was taken. The reversal is now owed.</summary>
    public void BeginReversal(Guid reversalCommandId, string code, string? detail, TimeProvider clock)
    {
        // Also reachable from PremiumRefundDue: an operator re-attempting a refund that the CBS
        // refused once. The attempt counter is what distinguishes the two on screen.
        Require(nameof(BeginReversal), SubscriptionStatus.Debited, SubscriptionStatus.PremiumRefundDue);

        if (CbsDebitReference is null)
            throw new DomainException(
                "A reversal needs the original debit's CBS reference; without it the CBS cannot "
                + "be asked to undo anything.");

        ReversalCommandId = reversalCommandId;
        ReversalAttempts++;
        Fail(code, detail);
        Status = SubscriptionStatus.Reversing;
        Touch(clock);
    }

    /// <summary>The customer has their money back. Terminal — a clean failure.</summary>
    public void MarkReversed(string? cbsReversalReference, TimeProvider clock)
    {
        Require(SubscriptionStatus.Reversing, nameof(MarkReversed));

        CbsReversalReference = string.IsNullOrWhiteSpace(cbsReversalReference)
            ? null
            : cbsReversalReference.Trim();

        ReversedAt = clock.GetUtcNow();
        Status = SubscriptionStatus.Reversed;
        Touch(clock);
    }

    /// <summary>
    /// <b>The reversal failed.</b> The premium is with the institution and the customer holds no
    /// policy. See <see cref="SubscriptionStatus.PremiumRefundDue"/> for why this is a named state
    /// and not an inference from the last command's status.
    /// </summary>
    public void MarkRefundDue(string code, string? detail, TimeProvider clock)
    {
        Require(SubscriptionStatus.Reversing, nameof(MarkRefundDue));

        Fail(code, detail);
        Status = SubscriptionStatus.PremiumRefundDue;
        Touch(clock);
    }

    /// <summary>
    /// Abandoned before the debit left. Only from <see cref="SubscriptionStatus.PendingDebit"/>:
    /// once money has moved there is nothing to cancel, only something to refund.
    /// </summary>
    public void Cancel(string? reason, TimeProvider clock)
    {
        Require(SubscriptionStatus.PendingDebit, nameof(Cancel));

        FailureDetail = reason;
        Status = SubscriptionStatus.Cancelled;
        Touch(clock);
    }

    /// <summary>
    /// True while nothing has been debited — the only window in which a subscription can be
    /// abandoned without owing the customer anything.
    /// </summary>
    public bool IsCancellable => Status == SubscriptionStatus.PendingDebit;

    /// <summary>
    /// True when the institution holds money for a contract that does not exist. One property so
    /// no caller re-derives the condition and gets it subtly wrong.
    /// </summary>
    public bool OwesRefund => Status == SubscriptionStatus.PremiumRefundDue;

    private void Fail(string code, string? detail)
    {
        FailureCode = string.IsNullOrWhiteSpace(code) ? IntegrationErrors.Rejected : code.Trim();
        FailureDetail = detail;
    }

    private void Touch(TimeProvider clock)
    {
        ArgumentNullException.ThrowIfNull(clock);
        UpdatedAt = clock.GetUtcNow();
    }

    private void Require(SubscriptionStatus expected, string operation)
        => Require(operation, expected);

    /// <summary>
    /// The transition table, enforced by <b>throwing</b> and not by returning a result — the same
    /// choice <see cref="IntegrationCommand"/> makes, and for the same reason: a step that fires
    /// out of order is a defect in the chaining, not an outcome a caller can handle. Swallowing it
    /// as a <c>Result</c> would let a redelivered debit-succeeded event mark a reversed
    /// subscription as issued.
    /// </summary>
    private void Require(string operation, params SubscriptionStatus[] allowed)
    {
        if (Array.IndexOf(allowed, Status) >= 0) return;

        throw new DomainException(
            $"{operation} is not allowed on a subscription in state {Status} "
            + $"(expected one of: {string.Join(", ", allowed)}).");
    }
}
