namespace Sankore.Modules.Integration.Adapters.PerfectVision;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Sankore.Modules.Integration.Domain;
using Sankore.Modules.Integration.Infrastructure;
using Sankore.Modules.Integration.Ports;
using Sankore.Modules.Integration.PublicApi;
using Sankore.Shared.Kernel;

/// <summary>
/// Perfect Vision — batch-file core banking, plus an optional read-only balance view (INT-28).
///
/// ───────────────────────────────────────────────────────────────────────────────────────────────
/// <b>WHAT IS MISSING.</b> Perfect Vision's interface specification. No document in this
/// repository defines the file layout, its field names, its encoding, the shape of the
/// acknowledgement it writes back, or the columns of the balance view. INT-28's own last
/// acceptance criterion says so: « Prérequis : spécification d'interface Perfect Vision obtenue
/// (question ouverte) », and <see cref="PerfectVisionSpecification.PlanReference"/> records it
/// next to the three other adapters in the same state.
///
/// <b>WHAT ARRIVES WITH IT.</b> A file of field mappings and a parser — not an architecture. The
/// capability matrix, the balance-source decision, the keyed registration, the ports, the health
/// answer and the test harness are all here and all behave. What every port method does instead
/// of building a record is return
/// <see cref="IntegrationErrors.AdapterSpecificationPending"/>, naming the missing document. The
/// day the specification lands, the work is: one mapping type per record, a writer onto the
/// INT-24/INT-25 batch socle, an acknowledgement parser, and the balance query — and this class
/// loses its refusals one method at a time, with the matrix and the registration untouched.
///
/// <b>THE THREE THINGS TO SETTLE WITH THE VENDOR FIRST</b>, in this order (the full wording is in
/// <see cref="PerfectVisionSpecification.OpenQuestions"/>, so that a procurement conversation and
/// this class quote the same list):
/// <list type="number">
/// <item>the outbound file layout and its encoding — record types, fields, order, code page;</item>
/// <item>the acknowledgement format — what comes back, where, and how a line identifies the
///   record it answers; without it a command can be sent and never closed;</item>
/// <item>whether the read-only balance view exists in this installation, and its exact
///   columns.</item>
/// </list>
/// ───────────────────────────────────────────────────────────────────────────────────────────────
///
/// <para>
/// <b>Why a refusal and not a plausible mapping.</b> M02's biometry client was hand-written
/// against a service nobody had read the document of: not one field name matched, every call
/// mapped to null and answered a generic error, and every KYC file sat in <c>Verifying</c> while
/// the service answered perfectly. Here the equivalent mistake is quieter and worse — a batch file
/// in the wrong layout is deposited successfully, the SFTP transfer reports success, and the
/// command waits for an acknowledgement that will never come because Perfect Vision could not
/// read what we sent. An explicit <see cref="ErrorFamily.Technical"/> refusal is the honest
/// state, and <c>Technical</c> specifically: the dispatcher never retries it (retrying cannot
/// obtain a document) and alerts the administrator instead.
/// </para>
///
/// <para>
/// <b>What it does NOT claim.</b> <c>ICbsTransactionPort</c> and <c>ICbsKycLevelPort</c> are not
/// implemented at all. That is deliberate and stronger than declaring them and refusing: a
/// batch-file core banking system answers no query, so transaction history, monthly flow and the
/// KYC tier come from the INT-21 snapshot — which is what <c>ICbsKycLevelPort</c>'s own remarks
/// describe. Not claiming an interface says "never"; declaring it and refusing would say "not
/// yet".
/// </para>
///
/// <para>
/// <b>No call journal.</b> Every other adapter wraps its calls in <c>ICallJournal</c> (INT-08).
/// This one makes no outbound call, and a journal row for a call that never touched a back-office
/// would read as a failure of Perfect Vision rather than as a missing document on our side.
/// The refusal is returned to the dispatcher, which records it on the command where an
/// administrator is already looking.
/// </para>
/// </summary>
internal sealed class PerfectVisionAdapter(
    IntegrationDbContext db,
    ITenantContext tenant,
    TimeProvider clock,
    ILogger<PerfectVisionAdapter> logger)
    : ICbsAdapter, ICbsCustomerPort, ICbsAccountPort, ICbsLoanPort
{
    private bool _settingsResolved;
    private PerfectVisionSettings? _settings;

    public IntegrationKind Kind => IntegrationKind.PerfectVision;

    /// <summary>
    /// The matrix of <see cref="PerfectVisionCapabilityMatrix"/>, applied to this tenant's own
    /// Perfect Vision connection — the batch writes always, a live balance read only where the
    /// installation exposes a view.
    /// </summary>
    public IntegrationCapabilities Capabilities => PerfectVisionCapabilityMatrix.For(Settings);

    // ── Settings ────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The tenant's Perfect Vision settings, read once per scope.
    ///
    /// <para>
    /// <b>Read synchronously, and that is the lesser evil.</b> <c>ICbsAdapter.Capabilities</c> is
    /// a synchronous property on a contract this adapter may not change, and INT-28's criterion 3
    /// requires the matrix to depend on the row. The alternatives are worse: blocking on an async
    /// query with <c>GetAwaiter().GetResult()</c> is the deadlock-prone form of exactly this, and
    /// reading in the constructor would query for every resolution of the adapter including the
    /// ones that never look at the matrix. So: one bounded single-row read, taken lazily, cached
    /// for the lifetime of the scope. Temenos keeps a fixed matrix and needs none of this; INT-31
    /// will face the same question for the Amplitude version.
    /// </para>
    ///
    /// <para>
    /// <b>Activation is NOT part of the predicate</b>, unlike every other adapter's binding. A
    /// Perfect Vision connection can never be activated — its health check cannot pass until the
    /// specification arrives — so filtering on <c>IsActive</c> would make the matrix permanently
    /// empty and criterion 3 undeliverable. The matrix describes a configured installation; it is
    /// not a licence to send anything, and every port method refuses regardless. An active row
    /// still wins where a tenant has several, so the day activation becomes possible this reads
    /// the connection commands actually flow through.
    /// </para>
    ///
    /// <para>
    /// <c>IgnoreQueryFilters</c> paired with an explicit tenant predicate, as every background
    /// path in this module does: the adapter is reached from the Hangfire dispatcher, where the
    /// ambient tenant comes from <c>BackgroundJobContext.SetScope</c>, and neither guard is
    /// load-bearing alone.
    /// </para>
    /// </summary>
    private PerfectVisionSettings? Settings
    {
        get
        {
            if (_settingsResolved) return _settings;
            _settingsResolved = true;

            if (!tenant.HasTenant || tenant.CurrentTenantId == Guid.Empty) return null;

            var tenantId = tenant.CurrentTenantId;

            var connection = db.Connections
                .IgnoreQueryFilters()
                .Where(c => c.TenantId == tenantId && c.Kind == IntegrationKind.PerfectVision)
                .OrderByDescending(c => c.IsActive)
                .ThenBy(c => c.CreatedAt)
                .FirstOrDefault();

            if (connection is null) return null;

            _settings = connection.Settings as PerfectVisionSettings;

            if (_settings is null)
            {
                // A Perfect Vision row carrying another kind's settings is a corrupted
                // configuration, not a missing specification: the two send an administrator to
                // different screens, so they are told apart even though both end in no matrix.
                logger.LogError(
                    "Connection {ConnectionId} is a Perfect Vision row whose settings are not "
                    + "PerfectVisionSettings; its capability matrix will omit the balance view.",
                    connection.Id);
            }

            return _settings;
        }
    }

    // ── Health (INT-03) ─────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Answers, and answers honestly: <b>unhealthy</b>, because the integration cannot operate.
    ///
    /// <para>
    /// No exception, by design. This is called from the activation screen and from the
    /// <c>integration</c> health check, and a throwing adapter would turn a known supplier
    /// dependency into a 500 that reads like an outage of ours.
    /// </para>
    ///
    /// <para>
    /// <b>It also cannot be made to pass, and that is the point.</b>
    /// <c>IntegrationConnection.Activate</c> refuses without a successful check, so a Perfect
    /// Vision connection stays inactive — which is the correct fail-closed outcome: an active one
    /// would have the dispatcher queue customer creations, account openings and KYC writes that
    /// can never be built into a file, filling a tenant's command queue with work that only a
    /// vendor document can release.
    /// </para>
    ///
    /// <para>
    /// No latency is reported, since nothing was called. Reporting zero would put a Perfect Vision
    /// row in the activation screen's latency column next to systems that actually answered.
    /// </para>
    ///
    /// <para>
    /// The settings shape is still checked first. A row carrying another kind's settings is a
    /// different problem with a different fix, and collapsing the two would send an administrator
    /// to procurement over a configuration mistake they can correct themselves.
    /// </para>
    /// </summary>
    public Task<IntegrationHealth> CheckHealthAsync(
        IntegrationConnection connection, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(connection);

        var checkedAt = clock.GetUtcNow();

        if (connection.Settings is not PerfectVisionSettings)
        {
            return Task.FromResult(IntegrationHealth.Unhealthy(
                $"{IntegrationErrors.SettingsInvalid}: this Perfect Vision connection does not "
                + "carry Perfect Vision settings.",
                checkedAt));
        }

        return Task.FromResult(IntegrationHealth.Unhealthy(
            $"{IntegrationErrors.AdapterSpecificationPending}: {PerfectVisionSpecification.HealthDetail()}",
            checkedAt));
    }

    // ── ICbsCustomerPort (INT-12), batch ────────────────────────────────────────────────────

    /// <summary>
    /// Refuses, naming the missing document.
    ///
    /// <para>
    /// A customer record is the first thing the outbound file would carry and the one whose field
    /// list is most completely unknown: <c>CbsCustomerPayload</c> holds twenty fields, several of
    /// them coded (<c>IdDocumentType</c>, <c>Gender</c>, <c>AgencyCode</c>) and needing
    /// translation through <c>integration_mapping</c> — and a mapping table cannot be filled in
    /// without the target vocabulary either.
    /// </para>
    /// </summary>
    public Task<IntegrationResult<ExternalId>> CreateCustomerAsync(
        CbsCustomerPayload payload, IdempotencyKey key, CancellationToken ct)
        => Task.FromResult(Pending<ExternalId>(PerfectVisionOperations.CreateCustomer));

    /// <inheritdoc cref="CreateCustomerAsync"/>
    public Task<IntegrationResult> UpdateCustomerAsync(
        ExternalId id, CbsCustomerPayload payload, IdempotencyKey key, CancellationToken ct)
        => Task.FromResult(Pending(PerfectVisionOperations.UpdateCustomer));

    /// <inheritdoc cref="CreateCustomerAsync"/>
    public Task<IntegrationResult> SetKycLevelAsync(
        ExternalId id, KycLevel level, IdempotencyKey key, CancellationToken ct)
        => Task.FromResult(Pending(PerfectVisionOperations.SetKycLevel));

    // ── ICbsAccountPort (INT-13, ASS-05) ────────────────────────────────────────────────────

    /// <inheritdoc cref="CreateCustomerAsync"/>
    public Task<IntegrationResult<ExternalId>> OpenAccountAsync(
        ExternalId customerId, string productCode, IdempotencyKey key, CancellationToken ct)
        => Task.FromResult(Pending<ExternalId>(PerfectVisionOperations.OpenAccount));

    /// <summary>
    /// Refuses. Reading accounts back is not a declared capability — a batch CBS answers no
    /// query, and Customer 360 is served by the INT-21 snapshot — so
    /// <c>IntegrationAdapterResolver.ResolvePort</c> stops the call before it reaches here. This
    /// body is the belt to that braces, and it still names the specification rather than the
    /// capability, because the inbound extraction that would feed such a read is defined by the
    /// same document.
    /// </summary>
    public Task<IntegrationResult<IReadOnlyList<CbsAccount>>> GetAccountsAsync(
        ExternalId customerId, CancellationToken ct)
        => Task.FromResult(Pending<IReadOnlyList<CbsAccount>>(PerfectVisionOperations.ReadAccounts));

    /// <summary>
    /// INT-28, criterion 2 — <b>the branch, which is ours, is real; the query, which is the
    /// vendor's, is not.</b>
    ///
    /// <list type="bullet">
    /// <item><b>No view configured</b> → <see cref="IntegrationErrors.CapabilityNotSupported"/>.
    ///   That is the honest statement: this adapter cannot read a live balance at all, and the
    ///   caller must use the snapshot. It is not a document we are waiting for — an installation
    ///   without a view will never serve a live balance, however complete the file
    ///   specification becomes. <c>IntegrationModuleFacade</c> never even arrives here in that
    ///   case: <c>ReadBalance</c> is absent from the matrix, so it goes straight to the stale
    ///   snapshot figure with <c>IsStale</c> set.</item>
    /// <item><b>A view configured</b> → <see cref="IntegrationErrors.AdapterSpecificationPending"/>.
    ///   The read is possible in principle and declared <c>RealTime</c> in the matrix; what is
    ///   missing is the view's columns. The facade attempts the live read, receives this, logs it
    ///   and falls back to the snapshot — degraded, labelled, and never a wrong figure.</item>
    /// </list>
    ///
    /// <para>
    /// Writing the query from a guessed column list is the one thing this method must not do. A
    /// balance is read at a counter, in front of a client, and a column guessed right in name but
    /// wrong in meaning — ledger balance where the available balance was wanted — answers
    /// confidently with a number that is simply not the one the clerk needs.
    /// </para>
    /// </summary>
    public Task<IntegrationResult<CbsBalance>> GetBalanceAsync(
        ExternalId accountId, CancellationToken ct)
    {
        var source = PerfectVisionBalanceRouting.ChooseFor(Settings);

        return Task.FromResult(source switch
        {
            PerfectVisionBalanceSource.ReadOnlyView =>
                Pending<CbsBalance>(PerfectVisionOperations.ReadBalance),

            _ => IntegrationResult.Technical<CbsBalance>(
                IntegrationErrors.CapabilityNotSupported,
                "This Perfect Vision connection configures no read-only balance view "
                + "(PerfectVisionSettings.BalanceViewName is empty), so the adapter cannot read a "
                + "live balance: the figure comes from the INT-21 snapshot, which reports it as "
                + "stale."),
        });
    }

    /// <summary>
    /// Refuses. The premium debit of ASS-05 is not a declared capability of this adapter, and
    /// whether a batch-file CBS even accepts a debit instruction in its outbound file is part of
    /// what the layout would say — so the refusal names the specification rather than claiming
    /// the operation does not exist.
    /// </summary>
    public Task<IntegrationResult<CbsDebitReceipt>> DebitAccountAsync(
        ExternalId accountId, decimal amount, string currency, string label,
        IdempotencyKey key, CancellationToken ct)
        => Task.FromResult(Pending<CbsDebitReceipt>(PerfectVisionOperations.DebitAccount));

    /// <inheritdoc cref="DebitAccountAsync"/>
    public Task<IntegrationResult<CbsDebitReceipt>> ReverseDebitAsync(
        ExternalId accountId, string originalReference, IdempotencyKey key, CancellationToken ct)
        => Task.FromResult(Pending<CbsDebitReceipt>(PerfectVisionOperations.ReverseDebit));

    // ── ICbsLoanPort, batch ─────────────────────────────────────────────────────────────────

    /// <inheritdoc cref="CreateCustomerAsync"/>
    public Task<IntegrationResult<ExternalId>> SubmitLoanApplicationAsync(
        CbsLoanApplicationPayload payload, IdempotencyKey key, CancellationToken ct)
        => Task.FromResult(Pending<ExternalId>(PerfectVisionOperations.SubmitLoanApplication));

    /// <inheritdoc cref="GetAccountsAsync"/>
    public Task<IntegrationResult<IReadOnlyList<CbsLoan>>> GetLoansAsync(
        ExternalId customerId, CancellationToken ct)
        => Task.FromResult(Pending<IReadOnlyList<CbsLoan>>(PerfectVisionOperations.ReadLoans));

    // ── The refusal, in one place ───────────────────────────────────────────────────────────

    /// <summary>
    /// <see cref="ErrorFamily.Technical"/> and never <see cref="ErrorFamily.Transient"/>. The
    /// family decides what the platform does next: <c>Technical</c> parks the command and alerts
    /// an administrator, which is right, while <c>Transient</c> would have the dispatcher retry a
    /// missing document with exponential backoff forever.
    /// </summary>
    private static IntegrationResult Pending(string operation)
        => IntegrationResult.Technical(
            IntegrationErrors.AdapterSpecificationPending,
            PerfectVisionSpecification.RefusalDetail(operation));

    /// <inheritdoc cref="Pending(string)"/>
    private static IntegrationResult<T> Pending<T>(string operation)
        => IntegrationResult.Technical<T>(
            IntegrationErrors.AdapterSpecificationPending,
            PerfectVisionSpecification.RefusalDetail(operation));
}
