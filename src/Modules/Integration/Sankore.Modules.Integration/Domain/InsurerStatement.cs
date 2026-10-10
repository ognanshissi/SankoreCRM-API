namespace Sankore.Modules.Integration.Domain;

using Sankore.Shared.Kernel;

/// <summary>
/// One month's <i>bordereau</i> for one insurer, and the commission it earns (ASS-10).
///
/// <para>
/// <c>ux_ins_statement_period</c> — <c>(tenant_id, connection_id, period)</c> — is the whole
/// guarantee: one statement per insurer per month, so a regeneration edits the draft instead of
/// producing a second document with different figures. All three columns are NOT NULL.
/// </para>
///
/// <para>
/// The totals are columns and not a view over the lines. A <see cref="StatementStatus.Finalised"/>
/// statement is a document the institution has justified to its insurer, and a view would
/// re-render last month's figures from today's data — a cancelled policy or an edited commission
/// rate would silently change a number somebody has already signed. The lines are the detail; the
/// header is the statement.
/// </para>
/// </summary>
public sealed class InsurerStatement : AggregateRoot
{
    public Guid Id { get; private set; }

    /// <summary>The insurer. Intra-schema FK.</summary>
    public Guid ConnectionId { get; private set; }

    /// <summary>The month, as <c>YYYY-MM</c>. Seven characters, the shape <c>KycLimitAlert</c> uses.</summary>
    public string Period { get; private set; } = string.Empty;

    public StatementStatus Status { get; private set; }

    // ── What ASS-10's first criterion asks a bordereau to carry ─────────────

    /// <summary>Adhésions: policies issued in the period.</summary>
    public int SubscriptionCount { get; private set; }

    /// <summary>Résiliations: policies cancelled or lapsed in the period.</summary>
    public int CancellationCount { get; private set; }

    /// <summary>Contre-passations: premiums given back.</summary>
    public int ReversalCount { get; private set; }

    /// <summary>Primes encaissées — the sum of the premiums actually debited.</summary>
    public decimal PremiumCollected { get; private set; }

    /// <summary>The part that went back. Kept separate rather than netted: a bordereau has to show both.</summary>
    public decimal PremiumReversed { get; private set; }

    /// <summary>
    /// The IMF's commission (ASS-10, criterion 3), summed from the lines — each of which applied
    /// the product's rate as it stood when the line was written.
    /// </summary>
    public decimal CommissionAmount { get; private set; }

    public string Currency { get; private set; } = string.Empty;

    public DateTimeOffset GeneratedAt { get; private set; }

    public DateTimeOffset? FinalisedAt { get; private set; }

    public Guid? FinalisedBy { get; private set; }

    // ── Exports and transmission ────────────────────────────────────────────

    /// <summary>Reference of the encrypted CSV export, once produced.</summary>
    public string? CsvStorageRef { get; private set; }

    /// <summary>Reference of the encrypted PDF export, once produced.</summary>
    public string? PdfStorageRef { get; private set; }

    /// <summary>
    /// The outbound batch file that carried it to the insurer, when the insurer accepts one. A
    /// plain Guid into <c>integration_batch_file</c> with no FK — the batch socle purges old files
    /// on its own retention, and a cascade would take the statement with them.
    /// </summary>
    public Guid? TransmitBatchFileId { get; private set; }

    public DateTimeOffset? TransmittedAt { get; private set; }

    /// <summary>Why a transmission failed. Never a payload value.</summary>
    public string? TransmitFailureDetail { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    public uint Version { get; private set; }

    private InsurerStatement() { }

    public static InsurerStatement Create(
        Guid tenantId,
        Guid connectionId,
        string period,
        string currency,
        TimeProvider clock,
        Guid? id = null)
    {
        if (tenantId == Guid.Empty) throw new DomainException("TenantId is required.");
        if (connectionId == Guid.Empty) throw new DomainException("ConnectionId is required.");
        if (string.IsNullOrWhiteSpace(period) || period.Trim().Length != 7)
            throw new DomainException("A statement period is a month, written YYYY-MM.");
        if (string.IsNullOrWhiteSpace(currency))
            throw new DomainException("A statement requires its currency.");
        ArgumentNullException.ThrowIfNull(clock);

        var now = clock.GetUtcNow();

        return new InsurerStatement
        {
            Id = id ?? Guid.NewGuid(),
            TenantId = tenantId,
            ConnectionId = connectionId,
            Period = period.Trim(),
            Currency = currency.Trim().ToUpperInvariant(),
            Status = StatementStatus.Draft,
            GeneratedAt = now,
            UpdatedAt = now,
        };
    }

    /// <summary>
    /// Replaces the header totals after a (re)generation. Only while
    /// <see cref="StatementStatus.Draft"/>: once finalised the figures are the ones the institution
    /// stands behind, and a regeneration must produce a new period's statement or be refused — not
    /// quietly restate a justified month.
    /// </summary>
    public void ApplyTotals(
        int subscriptionCount,
        int cancellationCount,
        int reversalCount,
        decimal premiumCollected,
        decimal premiumReversed,
        decimal commissionAmount,
        TimeProvider clock)
    {
        if (Status != StatementStatus.Draft)
            throw new DomainException(
                $"A {Status} statement cannot be recomputed: its figures have already been "
                + "justified to the insurer.");

        ArgumentNullException.ThrowIfNull(clock);

        SubscriptionCount = subscriptionCount;
        CancellationCount = cancellationCount;
        ReversalCount = reversalCount;
        PremiumCollected = premiumCollected;
        PremiumReversed = premiumReversed;
        CommissionAmount = commissionAmount;
        GeneratedAt = clock.GetUtcNow();
        UpdatedAt = GeneratedAt;
    }

    public void AttachExports(string? csvStorageRef, string? pdfStorageRef, TimeProvider clock)
    {
        ArgumentNullException.ThrowIfNull(clock);

        CsvStorageRef = string.IsNullOrWhiteSpace(csvStorageRef) ? CsvStorageRef : csvStorageRef.Trim();
        PdfStorageRef = string.IsNullOrWhiteSpace(pdfStorageRef) ? PdfStorageRef : pdfStorageRef.Trim();
        UpdatedAt = clock.GetUtcNow();
    }

    public void Finalise(Guid actor, TimeProvider clock)
    {
        if (Status != StatementStatus.Draft)
            throw new DomainException($"A {Status} statement is already closed.");

        ArgumentNullException.ThrowIfNull(clock);

        Status = StatementStatus.Finalised;
        FinalisedAt = clock.GetUtcNow();
        FinalisedBy = actor;
        UpdatedAt = FinalisedAt.Value;
    }

    public void MarkTransmitted(Guid? batchFileId, TimeProvider clock)
    {
        if (Status is not (StatementStatus.Finalised or StatementStatus.TransmitFailed))
            throw new DomainException("Only a finalised statement may be transmitted.");

        ArgumentNullException.ThrowIfNull(clock);

        TransmitBatchFileId = batchFileId;
        TransmittedAt = clock.GetUtcNow();
        TransmitFailureDetail = null;
        Status = StatementStatus.Transmitted;
        UpdatedAt = TransmittedAt.Value;
    }

    public void MarkTransmitFailed(string? detail, TimeProvider clock)
    {
        if (Status is not (StatementStatus.Finalised or StatementStatus.TransmitFailed))
            throw new DomainException("Only a finalised statement may fail transmission.");

        ArgumentNullException.ThrowIfNull(clock);

        TransmitFailureDetail = detail;
        Status = StatementStatus.TransmitFailed;
        UpdatedAt = clock.GetUtcNow();
    }

    /// <summary>True while the lines may still be deleted and rewritten.</summary>
    public bool IsRebuildable => Status == StatementStatus.Draft;
}

/// <summary>
/// One accounted movement on a statement (ASS-10, criterion 1).
///
/// <para>
/// <b>Every line is a snapshot, not a join.</b> It carries the policy number, the CBS reference,
/// the amount, the rate it applied and the commission it produced, as they were when the line was
/// written. The temptation is to keep ids only and render the figures from the live rows, and it is
/// wrong for the reason the header totals are columns: a bordereau is a justification document,
/// and a document whose numbers move when the catalogue is edited justifies nothing.
/// </para>
///
/// <para>
/// <b>No xmin.</b> A line is written once and never edited — a draft statement is rebuilt by
/// deleting its lines and writing them again, which is a delete and an insert, not an update. A
/// concurrency token here would claim a second writer exists.
/// </para>
/// </summary>
public sealed class InsurerStatementLine : AggregateRoot
{
    public Guid Id { get; private set; }

    /// <summary>Intra-schema FK to <c>ins_statement</c>, cascading: a line is part of its statement.</summary>
    public Guid StatementId { get; private set; }

    public StatementLineType LineType { get; private set; }

    /// <summary>
    /// The policy. Intra-schema FK — every line concerns a contract, and a line pointing at
    /// nothing could not be reconciled against the insurer's own bordereau.
    /// </summary>
    public Guid PolicyId { get; private set; }

    /// <summary>
    /// The instalment, for a <see cref="StatementLineType.PremiumPaid"/> line. Null on a
    /// subscription, a reversal or a cancellation line — which is exactly why
    /// <c>ux_ins_statement_line</c> carries <c>NULLS NOT DISTINCT</c>: PostgreSQL treats NULLs as
    /// distinct by default, so without it a double generation would duplicate every line whose
    /// instalment is null, and the control case — the premium lines — would be correctly refused,
    /// which is precisely what made the same hole invisible in
    /// <c>ux_integration_reconciliation_gap_open</c>.
    /// </summary>
    public Guid? InstalmentId { get; private set; }

    /// <summary>The subscription, for the lines that have one. Informational, no FK.</summary>
    public Guid? SubscriptionId { get; private set; }

    /// <summary>The day the movement happened — what orders a printed bordereau.</summary>
    public DateOnly OccurredOn { get; private set; }

    public decimal Amount { get; private set; }

    public string Currency { get; private set; } = string.Empty;

    /// <summary>The commission rate APPLIED, copied from the product at write time.</summary>
    public decimal CommissionRate { get; private set; }

    public decimal CommissionAmount { get; private set; }

    /// <summary>The contract number as printed. Snapshotted — see the class remarks.</summary>
    public string PolicyNumber { get; private set; } = string.Empty;

    /// <summary>The insurer's product code, snapshotted.</summary>
    public string InsurerProductCode { get; private set; } = string.Empty;

    /// <summary>The CBS reference of the movement, snapshotted. Blank for a cancellation.</summary>
    public string? CbsReference { get; private set; }

    /// <summary>Opaque reference to M01's customer, so a line is readable on its own.</summary>
    public Guid CrmCustomerId { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    private InsurerStatementLine() { }

    public static InsurerStatementLine Create(
        Guid tenantId,
        Guid statementId,
        StatementLineType lineType,
        Guid policyId,
        Guid crmCustomerId,
        DateOnly occurredOn,
        decimal amount,
        string currency,
        decimal commissionRate,
        string policyNumber,
        string insurerProductCode,
        TimeProvider clock,
        Guid? instalmentId = null,
        Guid? subscriptionId = null,
        string? cbsReference = null,
        Guid? id = null)
    {
        if (tenantId == Guid.Empty) throw new DomainException("TenantId is required.");
        if (statementId == Guid.Empty) throw new DomainException("StatementId is required.");
        if (policyId == Guid.Empty) throw new DomainException("PolicyId is required.");
        if (string.IsNullOrWhiteSpace(currency))
            throw new DomainException("A statement line requires its currency.");
        if (commissionRate is < 0m or > 1m)
            throw new DomainException("A commission rate is a fraction between 0 and 1.");
        ArgumentNullException.ThrowIfNull(clock);

        // Rounded to the currency's minor unit here rather than at render time: the header total is
        // the SUM of these lines, and a rate applied to unrounded amounts gives a total that does
        // not equal the printed detail — the one arithmetic error an insurer always finds.
        var commission = Math.Round(amount * commissionRate, 2, MidpointRounding.ToEven);

        return new InsurerStatementLine
        {
            Id = id ?? Guid.NewGuid(),
            TenantId = tenantId,
            StatementId = statementId,
            LineType = lineType,
            PolicyId = policyId,
            InstalmentId = instalmentId,
            SubscriptionId = subscriptionId,
            CrmCustomerId = crmCustomerId,
            OccurredOn = occurredOn,
            Amount = amount,
            Currency = currency.Trim().ToUpperInvariant(),
            CommissionRate = commissionRate,
            CommissionAmount = commission,
            PolicyNumber = policyNumber?.Trim() ?? string.Empty,
            InsurerProductCode = insurerProductCode?.Trim() ?? string.Empty,
            CbsReference = cbsReference?.Trim(),
            CreatedAt = clock.GetUtcNow(),
        };
    }
}
