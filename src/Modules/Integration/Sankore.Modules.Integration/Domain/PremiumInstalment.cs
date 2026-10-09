namespace Sankore.Modules.Integration.Domain;

using Sankore.Shared.Kernel;

/// <summary>
/// One premium due date on one policy (ASS-08).
///
/// <para>
/// <b>There is no separate "premium schedule" table.</b> The schedule is entirely determined by
/// the policy's periodicity, effective date and premium — three columns already on
/// <see cref="PolicyRecord"/> — so a schedule table would hold nothing but a restatement of them
/// and a second place for them to disagree. The instalments ARE the schedule, materialised one row
/// per due date, because that is the grain at which a debit is attempted, retried, declared unpaid
/// and finally justified on a statement.
/// </para>
///
/// <para>
/// <c>ux_ins_premium_instalment_due</c> — <c>(tenant_id, policy_id, due_date)</c> — is what makes
/// ASS-08's daily job safe. The criterion says the job creates a debit command for every due
/// instalment, and Hangfire guarantees at-least-once: run twice on the same morning and a
/// read-then-write check would debit twice. All three columns are NOT NULL, so PostgreSQL's
/// NULLS-DISTINCT behaviour cannot quietly let a duplicate through — the hole that
/// <c>ux_integration_reconciliation_gap_open</c> had to be rebuilt to close.
/// </para>
/// </summary>
public sealed class PremiumInstalment : AggregateRoot
{
    public Guid Id { get; private set; }

    /// <summary>Intra-schema FK to <c>ins_policy</c>, cascading.</summary>
    public Guid PolicyId { get; private set; }

    /// <summary>1-based order on the policy. Printed on a statement; not the key.</summary>
    public int SequenceNo { get; private set; }

    public DateOnly DueDate { get; private set; }

    public decimal Amount { get; private set; }

    public string Currency { get; private set; } = string.Empty;

    public InstalmentStatus Status { get; private set; }

    /// <summary>
    /// How many debits have been attempted. Compared with the PRODUCT's
    /// <see cref="InsuranceProduct.PremiumRetryLimit"/> — ASS-08's « politique paramétrable par
    /// produit » — which is read at decision time rather than copied here, so an administrator
    /// raising the limit helps the instalments that are already failing.
    /// </summary>
    public int Attempts { get; private set; }

    public DateTimeOffset? LastAttemptAt { get; private set; }

    /// <summary>
    /// When the next attempt becomes due. Served, with the status, by
    /// <c>ix_ins_premium_instalment_due_attempt</c> — deliberately NOT prefixed by tenant, like
    /// <c>ix_integration_command_status_next_attempt</c>: the orchestrator asks "which tenants owe
    /// work" before it knows the tenant.
    /// </summary>
    public DateTimeOffset? NextAttemptAt { get; private set; }

    /// <summary>The debit command in flight, or the last one. Plain Guid, filtered index, no FK.</summary>
    public Guid? DebitCommandId { get; private set; }

    /// <summary>The CBS reference of the successful debit — what ASS-10's statement justifies.</summary>
    public string? CbsDebitReference { get; private set; }

    public DateTimeOffset? PaidAt { get; private set; }

    /// <summary>Error code of the last failed attempt. Never a payload value.</summary>
    public string? LastErrorCode { get; private set; }

    public string? LastErrorDetail { get; private set; }

    /// <summary>
    /// When the <i>impayé</i> was declared to the insurer, and through which command. Separate
    /// from the debit's own fields because declaring an unpaid premium is a different write to a
    /// different system.
    /// </summary>
    public DateTimeOffset? UnpaidDeclaredAt { get; private set; }

    public Guid? UnpaidCommandId { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }

    public uint Version { get; private set; }

    private PremiumInstalment() { }

    public static PremiumInstalment Create(
        Guid tenantId,
        Guid policyId,
        int sequenceNo,
        DateOnly dueDate,
        decimal amount,
        string currency,
        TimeProvider clock,
        Guid? id = null)
    {
        if (tenantId == Guid.Empty) throw new DomainException("TenantId is required.");
        if (policyId == Guid.Empty) throw new DomainException("PolicyId is required.");
        if (sequenceNo < 1) throw new DomainException("An instalment sequence starts at 1.");
        if (amount <= 0m) throw new DomainException("An instalment needs a positive amount.");
        if (string.IsNullOrWhiteSpace(currency))
            throw new DomainException("An instalment amount requires its currency.");
        ArgumentNullException.ThrowIfNull(clock);

        var now = clock.GetUtcNow();

        return new PremiumInstalment
        {
            Id = id ?? Guid.NewGuid(),
            TenantId = tenantId,
            PolicyId = policyId,
            SequenceNo = sequenceNo,
            DueDate = dueDate,
            Amount = amount,
            Currency = currency.Trim().ToUpperInvariant(),
            Status = InstalmentStatus.Scheduled,
            CreatedAt = now,
            UpdatedAt = now,
        };
    }

    /// <summary>The daily job queued a debit for this instalment.</summary>
    public void BeginDebit(Guid commandId, TimeProvider clock)
    {
        Require(nameof(BeginDebit), InstalmentStatus.Scheduled, InstalmentStatus.Failed);

        DebitCommandId = commandId;
        Attempts++;
        LastAttemptAt = clock.GetUtcNow();
        NextAttemptAt = null;
        Status = InstalmentStatus.Debiting;
        Touch(clock);
    }

    public void MarkPaid(string? cbsDebitReference, TimeProvider clock)
    {
        Require(nameof(MarkPaid), InstalmentStatus.Debiting);

        CbsDebitReference = string.IsNullOrWhiteSpace(cbsDebitReference)
            ? null
            : cbsDebitReference.Trim();

        PaidAt = clock.GetUtcNow();
        LastErrorCode = null;
        LastErrorDetail = null;
        Status = InstalmentStatus.Paid;
        Touch(clock);
    }

    /// <summary>
    /// The debit failed and the product still allows a retry. <paramref name="nextAttemptAt"/>
    /// comes from the product's interval, computed by the caller: the instalment does not read the
    /// product, so the policy stays in one place.
    /// </summary>
    public void MarkFailed(
        string code, string? detail, DateTimeOffset nextAttemptAt, TimeProvider clock)
    {
        Require(nameof(MarkFailed), InstalmentStatus.Debiting);

        LastErrorCode = code;
        LastErrorDetail = detail;
        NextAttemptAt = nextAttemptAt;
        Status = InstalmentStatus.Failed;
        Touch(clock);
    }

    /// <summary>
    /// Retries are exhausted and the <i>impayé</i> has been declared to the insurer (ASS-08,
    /// criterion 2). Reachable from <see cref="InstalmentStatus.Debiting"/> as well as from
    /// <see cref="InstalmentStatus.Failed"/>: the last attempt's failure is what spends the budget.
    /// </summary>
    public void DeclareUnpaid(Guid? unpaidCommandId, string? code, string? detail, TimeProvider clock)
    {
        Require(nameof(DeclareUnpaid), InstalmentStatus.Debiting, InstalmentStatus.Failed);

        UnpaidCommandId = unpaidCommandId;
        UnpaidDeclaredAt = clock.GetUtcNow();
        LastErrorCode = code ?? LastErrorCode;
        LastErrorDetail = detail ?? LastErrorDetail;
        NextAttemptAt = null;
        Status = InstalmentStatus.Unpaid;
        Touch(clock);
    }

    /// <summary>Written off by a human. Allowed from anything that is not already settled.</summary>
    public void Waive(string? reason, TimeProvider clock)
    {
        Require(
            nameof(Waive),
            InstalmentStatus.Scheduled, InstalmentStatus.Failed, InstalmentStatus.Unpaid);

        LastErrorDetail = reason;
        NextAttemptAt = null;
        Status = InstalmentStatus.Waived;
        Touch(clock);
    }

    /// <summary>
    /// The policy ended before this fell due. Not allowed on a paid instalment: money that moved
    /// has to stay visible on the statement, and ASS-10 accounts for it as a reversal, not as a
    /// cancellation of the schedule.
    /// </summary>
    public void CancelBecausePolicyEnded(TimeProvider clock)
    {
        Require(
            nameof(CancelBecausePolicyEnded),
            InstalmentStatus.Scheduled, InstalmentStatus.Failed, InstalmentStatus.Unpaid);

        NextAttemptAt = null;
        Status = InstalmentStatus.Cancelled;
        Touch(clock);
    }

    /// <summary>True when a debit may still be attempted for this due date.</summary>
    public bool IsDebitable => Status is InstalmentStatus.Scheduled or InstalmentStatus.Failed;

    private void Touch(TimeProvider clock)
    {
        ArgumentNullException.ThrowIfNull(clock);
        UpdatedAt = clock.GetUtcNow();
    }

    /// <summary>
    /// Throws, like <see cref="InsuranceSubscription"/> and <see cref="IntegrationCommand"/>: an
    /// instalment moved out of order means the chaining is broken, and a <c>Result</c> would let a
    /// redelivered success mark a waived instalment paid.
    /// </summary>
    private void Require(string operation, params InstalmentStatus[] allowed)
    {
        if (Array.IndexOf(allowed, Status) >= 0) return;

        throw new DomainException(
            $"{operation} is not allowed on an instalment in state {Status} "
            + $"(expected one of: {string.Join(", ", allowed)}).");
    }
}
