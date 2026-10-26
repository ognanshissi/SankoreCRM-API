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

    // ── The contract it realises, and how its premium is paid ──────────────

    /// <summary>
    /// The CRM's contract record (M01 <c>ProductHolding</c>). A subscription opened from a holding
    /// starts when that holding is sent, and the insurer's answer is addressed to it. Opaque, no FK:
    /// it lives in another module's schema.
    /// </summary>
    public Guid? CrmHoldingId { get; private set; }

    /// <summary>The beneficiary clause version that was signed, sent with the holding.</summary>
    public int ClauseVersion { get; private set; } = 1;

    /// <summary>The won project whose questionnaires travel with the subscription. Opaque.</summary>
    public Guid? SourceOpportunityId { get; private set; }

    /// <summary>
    /// SHA-256 of the questionnaire answers as signed. Compared again just before the insurer is
    /// asked: an answer edited in between makes the declaration sent differ from the one signed.
    /// </summary>
    public string? AnswersSha256 { get; private set; }

    public PremiumPaymentMode PaymentMode { get; private set; } = PremiumPaymentMode.CoreBankingDebit;

    public RecordedPaymentMethod? RecordedPaymentMethod { get; private set; }

    /// <summary>The receipt, transfer or Mobile Money reference the agent recorded.</summary>
    public string? RecordedPaymentReference { get; private set; }

    public DateOnly? RecordedPaidOn { get; private set; }

    /// <summary>When the premium was collected — debited, recorded, or reported paid by the insurer.</summary>
    public DateTimeOffset? PremiumPaidAt { get; private set; }

    /// <summary>The 60-day derogation's deadline, set when the policy is issued.</summary>
    public DateOnly? PaymentDueBy { get; private set; }

    /// <summary>The subscriber's written commitment to pay within 60 days (derogation only).</summary>
    public string? CommitmentReference { get; private set; }

    public DateOnly? CommitmentSignedOn { get; private set; }

    /// <summary>The information sheet handed to the client before the signature.</summary>
    public Guid? InformationSheetDeliveryId { get; private set; }

    // ── Lot 3: the insurer's answer, its 15 days, the client's answer ───────

    /// <summary>When the insurer was asked: the start of its 15 days to answer.</summary>
    public DateTimeOffset? SentToInsurerAt { get; private set; }

    public DateTimeOffset? IssuedAt { get; private set; }

    public UnderwritingOutcome? Underwriting { get; private set; }
    public decimal? SurchargeAmount { get; private set; }
    public string? UnderwritingExclusions { get; private set; }
    public string? MedicalExamsRequested { get; private set; }

    /// <summary>The insurer's letter, portal reference or e-mail the decision was read from.</summary>
    public string? UnderwritingReference { get; private set; }
    public DateOnly? UnderwritingReceivedOn { get; private set; }
    public DateTimeOffset? UnderwritingRecordedAt { get; private set; }
    public Guid? UnderwritingRecordedBy { get; private set; }

    public ClientUnderwritingAnswer? ClientAnswer { get; private set; }
    public string? ClientAnswerReference { get; private set; }
    public DateTimeOffset? ClientAnsweredAt { get; private set; }

    public DateTimeOffset? SurchargePaidAt { get; private set; }

    // ── Lot 4: a third-party payer, a client of the institution ─────────────

    /// <summary>The client who pays the premium when it is not the subscriber; KYC approved at preparation.</summary>
    public Guid? PremiumPayerClientId { get; private set; }

    /// <summary>A surcharge or exclusions the client has not answered yet.</summary>
    public bool AwaitsClientAnswer
        => Underwriting is UnderwritingOutcome.AcceptedWithSurcharge or UnderwritingOutcome.AcceptedWithExclusions
        && ClientAnswer is null;

    /// <summary>An accepted surcharge the distributor collects and has not collected yet.</summary>
    public bool IsSurchargeOutstanding
        => Underwriting == UnderwritingOutcome.AcceptedWithSurcharge
        && ClientAnswer == ClientUnderwritingAnswer.Accepted
        && SurchargePaidAt is null
        && !IsEnded;

    /// <summary>Ended without a policy in force: refused, refunded, abandoned.</summary>
    public bool IsEnded => Status is SubscriptionStatus.Refused or SubscriptionStatus.Cancelled
        or SubscriptionStatus.DebitFailed or SubscriptionStatus.Reversed or SubscriptionStatus.PremiumRefundDue;

    /// <summary>
    /// The premium is still owed on a contract sent without it. The holding stays « en attente de
    /// paiement » while this is true.
    /// </summary>
    public bool IsPremiumOutstanding
        => PaymentMode is PremiumPaymentMode.InsurerCollects or PremiumPaymentMode.DeferredByDerogation
        && PremiumPaidAt is null
        && Status is not (SubscriptionStatus.Refused or SubscriptionStatus.Cancelled);

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
    /// A subscription attached to a signed holding, waiting for that holding to be sent
    /// (<see cref="SubscriptionStatus.Prepared"/>). How the premium is paid is decided now, with the
    /// client, and checked here: a debit needs an account, a recorded payment its method, reference
    /// and date.
    /// </summary>
    public static InsuranceSubscription Prepare(
        Guid tenantId,
        Guid connectionId,
        Guid crmCustomerId,
        Guid productId,
        DateOnly effectiveDate,
        decimal premiumAmount,
        string currency,
        Guid createdBy,
        TimeProvider clock,
        Guid holdingId,
        int clauseVersion,
        PremiumPaymentMode paymentMode,
        string? cbsAccountRef,
        RecordedPaymentMethod? paymentMethod,
        string? paymentReference,
        DateOnly? paidOn,
        Guid? sourceOpportunityId = null,
        string? answersSha256 = null,
        string? commitmentReference = null,
        DateOnly? commitmentSignedOn = null,
        Guid? informationSheetDeliveryId = null,
        Guid? premiumPayerClientId = null)
    {
        if (holdingId == Guid.Empty) throw new DomainException("HoldingId is required.");

        if (paymentMode == PremiumPaymentMode.DeferredByDerogation
            && (string.IsNullOrWhiteSpace(commitmentReference) || commitmentSignedOn is null))
            throw new DomainException(
                "The 60-day derogation needs the subscriber's written commitment: its reference and the day it was signed.");

        if (paymentMode == PremiumPaymentMode.CoreBankingDebit && string.IsNullOrWhiteSpace(cbsAccountRef))
            throw new DomainException("A premium debited from core banking needs the account to debit.");

        if (paymentMode == PremiumPaymentMode.RecordedByAgent
            && (paymentMethod is null || string.IsNullOrWhiteSpace(paymentReference) || paidOn is null))
            throw new DomainException(
                "A premium recorded by the agent needs its method, its reference and the day it was paid.");

        var subscription = Create(
            tenantId, connectionId, crmCustomerId, productId, effectiveDate, premiumAmount, currency,
            createdBy, clock,
            cbsAccountRef: paymentMode == PremiumPaymentMode.CoreBankingDebit ? cbsAccountRef : null);

        subscription.Status = SubscriptionStatus.Prepared;
        subscription.CrmHoldingId = holdingId;
        subscription.ClauseVersion = clauseVersion < 1 ? 1 : clauseVersion;
        subscription.SourceOpportunityId = sourceOpportunityId;
        subscription.AnswersSha256 = string.IsNullOrWhiteSpace(answersSha256) ? null : answersSha256;
        subscription.PaymentMode = paymentMode;
        subscription.InformationSheetDeliveryId = informationSheetDeliveryId;
        subscription.PremiumPayerClientId = premiumPayerClientId;

        if (paymentMode == PremiumPaymentMode.DeferredByDerogation)
        {
            subscription.CommitmentReference = commitmentReference!.Trim();
            subscription.CommitmentSignedOn = commitmentSignedOn;
        }

        if (paymentMode == PremiumPaymentMode.RecordedByAgent)
        {
            subscription.RecordedPaymentMethod = paymentMethod;
            subscription.RecordedPaymentReference = paymentReference!.Trim();
            subscription.RecordedPaidOn = paidOn;
        }

        return subscription;
    }

    /// <summary>The holding was sent and its premium is to be debited: the chain starts.</summary>
    public void StartWithDebit(Guid debitCommandId, TimeProvider clock)
    {
        Require(SubscriptionStatus.Prepared, nameof(StartWithDebit));

        if (PaymentMode != PremiumPaymentMode.CoreBankingDebit)
            throw new DomainException("This subscription's premium was recorded by the agent, not debited.");

        Status = SubscriptionStatus.PendingDebit;
        DebitCommandId = debitCommandId;
        Touch(clock);
    }

    /// <summary>
    /// The holding was sent and its premium was already collected by the agent: the insurer is
    /// asked next, with nothing to debit first.
    /// </summary>
    public void StartWithRecordedPayment(TimeProvider clock)
    {
        Require(SubscriptionStatus.Prepared, nameof(StartWithRecordedPayment));

        if (PaymentMode != PremiumPaymentMode.RecordedByAgent)
            throw new DomainException("This subscription's premium is to be debited, not recorded.");

        Status = SubscriptionStatus.PaymentRecorded;
        PremiumPaidAt = clock.GetUtcNow();
        Touch(clock);
    }

    /// <summary>
    /// The holding was sent and the insurer is asked before the premium is paid — the insurer
    /// collects it, or the derogation applies.
    /// </summary>
    public void StartAwaitingPremium(TimeProvider clock)
    {
        Require(SubscriptionStatus.Prepared, nameof(StartAwaitingPremium));

        if (PaymentMode is not (PremiumPaymentMode.InsurerCollects or PremiumPaymentMode.DeferredByDerogation))
            throw new DomainException("This subscription's premium is collected before the insurer is asked.");

        Status = SubscriptionStatus.AwaitingPremium;
        Touch(clock);
    }

    /// <summary>
    /// A premium still owed, collected afterwards by the distributor (under the insurer's collection
    /// mandate, which the caller checked).
    /// </summary>
    public void RecordLatePayment(RecordedPaymentMethod method, string reference, DateOnly paidOn, TimeProvider clock)
    {
        if (!IsPremiumOutstanding && !IsSurchargeOutstanding)
            throw new DomainException("No premium is outstanding on this subscription.");

        // An accepted surcharge collected after the premium: only the surcharge is paid now.
        if (!IsPremiumOutstanding)
        {
            if (string.IsNullOrWhiteSpace(reference))
                throw new DomainException("A premium recorded by the agent needs its reference.");

            SurchargePaidAt = clock.GetUtcNow();
            RecordedPaymentMethod ??= method;
            RecordedPaymentReference ??= reference.Trim();
            Touch(clock);
            return;
        }

        if (string.IsNullOrWhiteSpace(reference))
            throw new DomainException("A premium recorded by the agent needs its reference.");

        RecordedPaymentMethod = method;
        RecordedPaymentReference = reference.Trim();
        RecordedPaidOn = paidOn;
        PremiumPaidAt = clock.GetUtcNow();
        Touch(clock);
    }

    /// <summary>The insurer reported the premium paid (synchronisation). Idempotent.</summary>
    public bool MarkPremiumPaidByInsurer(TimeProvider clock)
    {
        if (!IsPremiumOutstanding) return false;

        PremiumPaidAt = clock.GetUtcNow();
        Touch(clock);
        return true;
    }

    /// <summary>
    /// The insurer's underwriting answer, read by the agent (lot 3). A refusal is recorded here and
    /// ended by <see cref="EndWithoutCover"/>; the silence of 15 days is only possible after them.
    /// </summary>
    public void RecordUnderwriting(
        UnderwritingOutcome outcome, decimal? surcharge, string? exclusions, string? exams,
        string reference, DateOnly receivedOn, Guid by, TimeProvider clock)
    {
        if (SentToInsurerAt is null)
            throw new DomainException("The insurer has not been asked yet.");
        if (IsEnded)
            throw new DomainException("This subscription has ended.");
        if (string.IsNullOrWhiteSpace(reference))
            throw new DomainException("The insurer's answer is recorded with its reference (letter, portal, e-mail).");
        if (outcome == UnderwritingOutcome.AcceptedWithSurcharge && surcharge is not > 0m)
            throw new DomainException("A surcharge is recorded with its amount.");
        if (outcome == UnderwritingOutcome.AcceptedWithExclusions && string.IsNullOrWhiteSpace(exclusions))
            throw new DomainException("Exclusions are recorded with their wording.");
        if (outcome == UnderwritingOutcome.MedicalExamsRequired && string.IsNullOrWhiteSpace(exams))
            throw new DomainException("The medical exams asked for are recorded.");

        var now = clock.GetUtcNow();
        if (outcome == UnderwritingOutcome.DeemedAcceptedBySilence)
        {
            if (Underwriting is not (null or UnderwritingOutcome.MedicalExamsRequired))
                throw new DomainException("The insurer already answered.");
            if (now < SentToInsurerAt.Value.AddDays(15))
                throw new DomainException("The insurer's 15 days to answer have not run out.");
        }

        Underwriting = outcome;
        SurchargeAmount = outcome == UnderwritingOutcome.AcceptedWithSurcharge ? surcharge : null;
        UnderwritingExclusions = outcome == UnderwritingOutcome.AcceptedWithExclusions ? exclusions!.Trim() : null;
        MedicalExamsRequested = outcome == UnderwritingOutcome.MedicalExamsRequired ? exams!.Trim() : null;
        UnderwritingReference = reference.Trim();
        UnderwritingReceivedOn = receivedOn;
        UnderwritingRecordedAt = now;
        UnderwritingRecordedBy = by;
        ClientAnswer = null;
        ClientAnswerReference = null;
        ClientAnsweredAt = null;
        Touch(clock);
    }

    /// <summary>The client's answer to a surcharge or exclusions. A refusal ends the subscription (caller).</summary>
    public void RecordClientAnswer(ClientUnderwritingAnswer answer, string reference, TimeProvider clock)
    {
        if (!AwaitsClientAnswer)
            throw new DomainException("No surcharge or exclusion awaits the client's answer.");
        if (string.IsNullOrWhiteSpace(reference))
            throw new DomainException("The client's answer is recorded with its reference (signed rider, letter).");

        ClientAnswer = answer;
        ClientAnswerReference = reference.Trim();
        ClientAnsweredAt = clock.GetUtcNow();
        Touch(clock);
    }

    /// <summary>
    /// Ends a subscription the insurer refused, or whose surcharge or exclusions the client declined,
    /// after it was sent. Nothing collected → Refused; a premium collected → owed back to the client.
    /// </summary>
    public void EndWithoutCover(string code, string? detail, TimeProvider clock)
    {
        if (IsEnded)
            throw new DomainException("This subscription has already ended.");

        Fail(code, detail);
        Status = PremiumPaidAt is null ? SubscriptionStatus.Refused : SubscriptionStatus.PremiumRefundDue;
        Touch(clock);
    }

    /// <summary>The insurer refused a subscription sent before its premium: nothing was collected.</summary>
    public void MarkRefused(string code, string? detail, TimeProvider clock)
    {
        Require(SubscriptionStatus.AwaitingPremium, nameof(MarkRefused));

        Fail(code, detail);
        Status = SubscriptionStatus.Refused;
        Touch(clock);
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
        PremiumPaidAt = DebitedAt;
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
        Require(nameof(AttachSubscribeCommand), SubscriptionStatus.Debited, SubscriptionStatus.PaymentRecorded, SubscriptionStatus.AwaitingPremium);
        SentToInsurerAt ??= clock.GetUtcNow();

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

        Require(nameof(MarkIssued), SubscriptionStatus.Debited, SubscriptionStatus.PaymentRecorded, SubscriptionStatus.AwaitingPremium);

        IssuedAt = clock.GetUtcNow();

        // The derogation's 60 days run from the issue of the policy.
        if (PaymentMode == PremiumPaymentMode.DeferredByDerogation && PremiumPaidAt is null)
            PaymentDueBy = DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime).AddDays(60);


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
        // From PaymentRecorded too: a premium the agent collected has no core banking debit to
        // reverse, so an insurer refusal leaves the institution owing it straight away.
        Require(nameof(MarkRefundDue), SubscriptionStatus.Reversing, SubscriptionStatus.PaymentRecorded);

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
        Require(nameof(Cancel), SubscriptionStatus.PendingDebit, SubscriptionStatus.Prepared);

        FailureDetail = reason;
        Status = SubscriptionStatus.Cancelled;
        Touch(clock);
    }

    /// <summary>
    /// True while nothing has been debited — the only window in which a subscription can be
    /// abandoned without owing the customer anything.
    /// </summary>
    public bool IsCancellable => Status is SubscriptionStatus.PendingDebit or SubscriptionStatus.Prepared;

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
