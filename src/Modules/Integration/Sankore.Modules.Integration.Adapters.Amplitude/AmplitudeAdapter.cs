namespace Sankore.Modules.Integration.Adapters.Amplitude;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Sankore.Modules.Integration.Domain;
using Sankore.Modules.Integration.Infrastructure;
using Sankore.Modules.Integration.Ports;
using Sankore.Modules.Integration.PublicApi;
using Sankore.Shared.Kernel;

/// <summary>
/// Amplitude (SBS) — <b>two integrations behind one kind</b>: API services on Amplitude Up, the
/// batch socle on every earlier release (INT-31).
///
/// ───────────────────────────────────────────────────────────────────────────────────────────────
/// <b>WHAT IS MISSING.</b> Amplitude's interface contract and API access. No document in this
/// repository defines a service of the Up catalogue, its request or response shape, its
/// authentication, the pre-Up file layout, its encoding, or the shape of the acknowledgement
/// Amplitude writes back. INT-31's own last acceptance criterion says so: « Prérequis : contrat
/// d'interface et accès API obtenus auprès de SBS », and
/// <see cref="AmplitudeSpecification.PlanReference"/> records it next to the three other adapters
/// in the same state.
///
/// <b>WHICH MAKES CRITERIA 1 AND 4 UNDELIVERABLE, AND 2 AND 3 DELIVERABLE IN FULL.</b> Criterion 1
/// (« l'adaptateur implémente les ports sur les services API de la version installée ») and
/// criterion 4 (« tests de contrat verts sur l'environnement de test ») both need SBS. Criterion 2
/// (« il bascule sur le socle batch, selon AmplitudeSettings.AmplitudeVersion ») and criterion 3
/// (« la matrice de capacités est calculée selon la version ») need nothing from anybody: they are
/// <see cref="AmplitudeCarrierRouting"/> and <see cref="AmplitudeCapabilityMatrix"/>, and they are
/// implemented and tested here.
///
/// <b>WHAT ARRIVES WITH THE CONTRACT.</b> A file of field mappings and either a client or a
/// writer — not an architecture. The capability matrix, the carrier decision, the coherence rule,
/// the keyed registration, the ports, the health answer and the test harness are all here and all
/// behave. What every port method does instead of building a request or a record is return
/// <see cref="IntegrationErrors.AdapterSpecificationPending"/>, naming the artefact this
/// installation's release needs. The day the contract lands, the work is: for Up, a generated or
/// hand-mapped client per service; for pre-Up, one mapping type per record, a writer onto the
/// INT-24/INT-25 batch socle and an acknowledgement parser — and this class loses its refusals one
/// method at a time, with the matrix and the registration untouched.
///
/// <b>THE QUESTIONS TO SETTLE WITH SBS</b>, in the order they unblock work, are in
/// <see cref="AmplitudeSpecification.OpenQuestions"/> — so that a procurement conversation and this
/// class quote the same list. The important structural point about them: <b>the Up catalogue and
/// the pre-Up file layout are two separate asks</b>, of different teams, about different releases,
/// and an answer to one unblocks nothing about the other. That is why every refusal names only the
/// artefact the installation in front of the operator actually needs.
/// ───────────────────────────────────────────────────────────────────────────────────────────────
///
/// <para>
/// <b>Why a refusal and not a plausible mapping.</b> M02's biometry client was hand-written against
/// a service nobody had read the document of: not one field name matched, every call mapped to null
/// and answered a generic error, and every KYC file sat in <c>Verifying</c> while the service
/// answered perfectly. Here the equivalent mistake has two quieter forms. On Up, a guessed endpoint
/// answers 404 and the write is at least seen to fail. On pre-Up it is worse and silent: a batch
/// file in the wrong layout is deposited successfully, the SFTP transfer reports success, and the
/// command waits out its <c>AckTimeoutHours</c> for an acknowledgement that will never come because
/// Amplitude could not read what we sent. An explicit <see cref="ErrorFamily.Technical"/> refusal is
/// the honest state, and <c>Technical</c> specifically: the dispatcher never retries it (retrying
/// cannot obtain a document) and alerts the administrator instead.
/// </para>
///
/// <para>
/// <b>What it does NOT claim.</b> <c>ICbsTransactionPort</c> and <c>ICbsKycLevelPort</c> are not
/// implemented at all. The reasoning differs from <c>PerfectVisionAdapter</c>'s and the difference
/// matters: that adapter serves ONE carrier, so not claiming an interface says "never". This one
/// serves two releases from one class, so its interface set is the UNION over releases and cannot
/// express a per-release difference — the matrix is the only place such a difference can live. The
/// two ports left unclaimed are therefore the ones NEITHER release serves through this adapter:
/// transaction history and monthly flow belong to INT-13/INT-22 and to no criterion of INT-31, and
/// the KYC tier read is impossible on a batch CBS and unevidenced on Up. Both absences are argued
/// capability by capability in <see cref="AmplitudeCapabilityMatrix.ApiReads"/>.
/// </para>
///
/// <para>
/// <b>No call journal.</b> Every other adapter wraps its calls in <c>ICallJournal</c> (INT-08).
/// This one makes no outbound call, and a journal row for a call that never touched a back-office
/// would read as a failure of Amplitude rather than as a missing document on our side. The refusal
/// is returned to the dispatcher, which records it on the command where an administrator is
/// already looking.
/// </para>
/// </summary>
internal sealed class AmplitudeAdapter(
    IntegrationDbContext db,
    ITenantContext tenant,
    TimeProvider clock,
    ILogger<AmplitudeAdapter> logger)
    : ICbsAdapter, ICbsCustomerPort, ICbsAccountPort, ICbsLoanPort
{
    private bool _bindingResolved;
    private AmplitudeSettings? _settings;
    private IntegrationMode? _mode;

    public IntegrationKind Kind => IntegrationKind.Amplitude;

    /// <summary>
    /// The matrix of <see cref="AmplitudeCapabilityMatrix"/>, applied to this tenant's own
    /// Amplitude connection — the five writes always, in the mode the release and the connection's
    /// carrier resolve to, plus the three live reads on an Up installation.
    /// </summary>
    public IntegrationCapabilities Capabilities => AmplitudeCapabilityMatrix.For(Settings, Mode);

    // ── The installation ────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The tenant's Amplitude settings, read once per scope.
    ///
    /// <para>
    /// <b>Read synchronously, and that is the lesser evil.</b> <c>ICbsAdapter.Capabilities</c> is a
    /// synchronous property on a contract this adapter may not change, and INT-31's criterion 3
    /// requires the matrix to depend on the row — this is the very case
    /// <c>PerfectVisionAdapter.Settings</c> predicted would come back for the Amplitude version.
    /// The alternatives are worse: blocking on an async query with <c>GetAwaiter().GetResult()</c>
    /// is the deadlock-prone form of exactly this, and reading in the constructor would query for
    /// every resolution of the adapter including the ones that never look at the matrix. So: one
    /// bounded single-row read, taken lazily, cached for the lifetime of the scope.
    /// </para>
    ///
    /// <para>
    /// <b>Activation is NOT part of the predicate</b>, unlike every other adapter's binding. An
    /// Amplitude connection can never be activated — its health check cannot pass until the
    /// contract arrives — so filtering on <c>IsActive</c> would make the matrix permanently empty
    /// and criterion 3 undeliverable. The matrix describes a configured installation; it is not a
    /// licence to send anything, and every port method refuses regardless. An active row still wins
    /// where a tenant has several, so the day activation becomes possible this reads the connection
    /// commands actually flow through.
    /// </para>
    ///
    /// <para>
    /// <c>IgnoreQueryFilters</c> paired with an explicit tenant predicate, as every background path
    /// in this module does: the adapter is reached from the Hangfire dispatcher, where the ambient
    /// tenant comes from <c>BackgroundJobContext.SetScope</c>, and neither guard is load-bearing
    /// alone.
    /// </para>
    /// </summary>
    private AmplitudeSettings? Settings
    {
        get
        {
            Bind();
            return _settings;
        }
    }

    /// <summary>
    /// The connection's mode — the second input of the matrix, read from the same row and in the
    /// same read as the settings.
    ///
    /// <para>
    /// <c>null</c> when this tenant has no Amplitude connection at all, which is NOT the same as
    /// <see cref="IntegrationMode.Batch"/>: "nothing is configured" and "files were chosen" are
    /// different states, and the matrix narrows on the first without claiming the second. A screen
    /// asks what Amplitude supports before anything exists, and it must get an answer rather than
    /// a fault.
    /// </para>
    /// </summary>
    private IntegrationMode? Mode
    {
        get
        {
            Bind();
            return _mode;
        }
    }

    private void Bind()
    {
        if (_bindingResolved) return;
        _bindingResolved = true;

        if (!tenant.HasTenant || tenant.CurrentTenantId == Guid.Empty) return;

        var tenantId = tenant.CurrentTenantId;

        var connection = db.Connections
            .IgnoreQueryFilters()
            .Where(c => c.TenantId == tenantId && c.Kind == IntegrationKind.Amplitude)
            .OrderByDescending(c => c.IsActive)
            .ThenBy(c => c.CreatedAt)
            .FirstOrDefault();

        if (connection is null) return;

        // The mode is kept even when the settings turn out to be of the wrong shape: it is read
        // from a column the mismatch cannot corrupt, and the matrix still narrows correctly from
        // it alone.
        _mode = connection.Mode;
        _settings = connection.Settings as AmplitudeSettings;

        if (_settings is null)
        {
            // An Amplitude row carrying another kind's settings is a corrupted configuration, not
            // a missing contract: the two send an administrator to different screens, so they are
            // told apart even though both end in a narrowed matrix.
            logger.LogError(
                "Connection {ConnectionId} is an Amplitude row whose settings are not "
                + "AmplitudeSettings; its capability matrix will omit the live reads and declare "
                + "the writes as batch.",
                connection.Id);
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
    /// <b>It also cannot be made to pass, and for Amplitude that is the only thing standing between
    /// a configured connection and an invented file.</b> <c>IntegrationConnection.Activate</c>
    /// refuses without a successful check, so an Amplitude connection stays inactive. The stake is
    /// sharper here than for Perfect Vision because Amplitude's pre-Up carrier is the shared batch
    /// socle and <c>ExecuteIntegrationCommandHandler</c> routes on the MODE <b>before</b> resolving
    /// an adapter: on an active Batch connection the commands would never reach this class at all,
    /// so none of its refusals would ever be consulted — the generic file writer would produce a
    /// deposit in a layout nobody has specified, the transfer would report success, and every
    /// command would wait out its <c>AckTimeoutHours</c>. The health gate is the only thing in that
    /// path, which is why this method must never return healthy and why a test pins exactly that.
    /// </para>
    ///
    /// <para>
    /// No latency is reported, since nothing was called. Reporting zero would put an Amplitude row
    /// in the activation screen's latency column next to systems that actually answered.
    /// </para>
    ///
    /// <para>
    /// <b>Two configuration faults are reported first, and separately.</b> A row carrying another
    /// kind's settings, and a release that contradicts the connection's mode. Both are mistakes an
    /// administrator can correct themselves, on their own screen, in one edit; the missing contract
    /// is a procurement conversation. Collapsing any of the three into the others would send
    /// somebody to SBS over a field they could have fixed, or — worse — have them wait for a
    /// document that would not fix anything.
    /// </para>
    /// </summary>
    public Task<IntegrationHealth> CheckHealthAsync(
        IntegrationConnection connection, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(connection);

        var checkedAt = clock.GetUtcNow();

        if (connection.Settings is not AmplitudeSettings settings)
        {
            return Task.FromResult(IntegrationHealth.Unhealthy(
                $"{IntegrationErrors.SettingsInvalid}: this Amplitude connection does not carry "
                + "Amplitude settings.",
                checkedAt));
        }

        // Checked on the connection passed in and not on the bound row: the health check runs
        // BEFORE activation, on the connection the administrator is editing, and the row this
        // adapter would bind to may be another one entirely.
        var incoherence = AmplitudeCarrierRouting.IncoherenceDetail(settings, connection.Mode);

        if (incoherence is not null)
        {
            return Task.FromResult(IntegrationHealth.Unhealthy(
                $"{IntegrationErrors.SettingsInvalid}: {incoherence}",
                checkedAt));
        }

        return Task.FromResult(IntegrationHealth.Unhealthy(
            $"{IntegrationErrors.AdapterSpecificationPending}: "
            + AmplitudeSpecification.HealthDetail(settings.AmplitudeVersion),
            checkedAt));
    }

    // ── ICbsCustomerPort (INT-12) ───────────────────────────────────────────────────────────

    /// <summary>
    /// Refuses, naming the artefact this installation's release needs.
    ///
    /// <para>
    /// A customer record is the first thing either carrier would move and the one whose shape is
    /// most completely unknown: <c>CbsCustomerPayload</c> holds twenty fields, several of them
    /// coded (<c>IdDocumentType</c>, <c>Gender</c>, <c>AgencyCode</c>) and needing translation
    /// through <c>integration_mapping</c> — and a mapping table cannot be filled in without the
    /// target vocabulary either, on either release.
    /// </para>
    /// </summary>
    public Task<IntegrationResult<ExternalId>> CreateCustomerAsync(
        CbsCustomerPayload payload, IdempotencyKey key, CancellationToken ct)
        => Task.FromResult(Pending<ExternalId>(AmplitudeOperations.CreateCustomer));

    /// <inheritdoc cref="CreateCustomerAsync"/>
    public Task<IntegrationResult> UpdateCustomerAsync(
        ExternalId id, CbsCustomerPayload payload, IdempotencyKey key, CancellationToken ct)
        => Task.FromResult(Pending(AmplitudeOperations.UpdateCustomer));

    /// <inheritdoc cref="CreateCustomerAsync"/>
    public Task<IntegrationResult> SetKycLevelAsync(
        ExternalId id, KycLevel level, IdempotencyKey key, CancellationToken ct)
        => Task.FromResult(Pending(AmplitudeOperations.SetKycLevel));

    // ── ICbsAccountPort (INT-13, ASS-05) ────────────────────────────────────────────────────

    /// <inheritdoc cref="CreateCustomerAsync"/>
    public Task<IntegrationResult<ExternalId>> OpenAccountAsync(
        ExternalId customerId, string productCode, IdempotencyKey key, CancellationToken ct)
        => Task.FromResult(Pending<ExternalId>(AmplitudeOperations.OpenAccount));

    /// <inheritdoc cref="ReadRefusal"/>
    public Task<IntegrationResult<IReadOnlyList<CbsAccount>>> GetAccountsAsync(
        ExternalId customerId, CancellationToken ct)
        => Task.FromResult(ReadRefusal<IReadOnlyList<CbsAccount>>(AmplitudeOperations.ReadAccounts));

    /// <inheritdoc cref="ReadRefusal"/>
    public Task<IntegrationResult<CbsBalance>> GetBalanceAsync(
        ExternalId accountId, CancellationToken ct)
        => Task.FromResult(ReadRefusal<CbsBalance>(AmplitudeOperations.ReadBalance));

    /// <summary>
    /// Refuses. The premium debit of ASS-05 is declared by neither release's matrix, and whether
    /// either carrier even accepts a debit instruction is part of what the contract would say — so
    /// the refusal names the missing artefact rather than claiming the operation does not exist.
    /// </summary>
    public Task<IntegrationResult<CbsDebitReceipt>> DebitAccountAsync(
        ExternalId accountId, decimal amount, string currency, string label,
        IdempotencyKey key, CancellationToken ct)
        => Task.FromResult(Pending<CbsDebitReceipt>(AmplitudeOperations.DebitAccount));

    /// <inheritdoc cref="DebitAccountAsync"/>
    public Task<IntegrationResult<CbsDebitReceipt>> ReverseDebitAsync(
        ExternalId accountId, string originalReference, IdempotencyKey key, CancellationToken ct)
        => Task.FromResult(Pending<CbsDebitReceipt>(AmplitudeOperations.ReverseDebit));

    // ── ICbsLoanPort ────────────────────────────────────────────────────────────────────────

    /// <inheritdoc cref="CreateCustomerAsync"/>
    public Task<IntegrationResult<ExternalId>> SubmitLoanApplicationAsync(
        CbsLoanApplicationPayload payload, IdempotencyKey key, CancellationToken ct)
        => Task.FromResult(Pending<ExternalId>(AmplitudeOperations.SubmitLoanApplication));

    /// <inheritdoc cref="ReadRefusal"/>
    public Task<IntegrationResult<IReadOnlyList<CbsLoan>>> GetLoansAsync(
        ExternalId customerId, CancellationToken ct)
        => Task.FromResult(ReadRefusal<IReadOnlyList<CbsLoan>>(AmplitudeOperations.ReadLoans));

    // ── The two refusals, in one place each ─────────────────────────────────────────────────

    /// <summary>
    /// INT-31, criterion 2 on the read side — <b>the branch, which is ours, is real; the service,
    /// which is SBS's, is not.</b> Two different honest answers, decided by the release:
    ///
    /// <list type="bullet">
    /// <item><b>Amplitude Up</b> → <see cref="IntegrationErrors.AdapterSpecificationPending"/>. The
    ///   read is possible in principle and declared <c>RealTime</c> in the matrix; what is missing
    ///   is the service catalogue. <c>IntegrationModuleFacade</c> attempts the live read, receives
    ///   this, logs it and falls back to the INT-21 snapshot — degraded, labelled
    ///   <c>IsStale</c>, and never a wrong figure.</item>
    /// <item><b>A pre-Up release</b> → <see cref="IntegrationErrors.CapabilityNotSupported"/>. That
    ///   is the honest statement: this installation cannot serve a live read at all, and it will
    ///   not however complete the file layout becomes — nothing is being waited for, so the code is
    ///   not the specification one. The facade never even arrives here: the read is absent from the
    ///   matrix, so it goes straight to the snapshot figure.</item>
    /// </list>
    ///
    /// <para>
    /// Writing a query or a request from a guessed shape is the one thing a read must not do. A
    /// balance is read at a counter, in front of a client, and a field guessed right in name but
    /// wrong in meaning — ledger balance where the available balance was wanted — answers
    /// confidently with a number that is simply not the one the clerk needs.
    /// </para>
    /// </summary>
    private IntegrationResult<T> ReadRefusal<T>(string operation)
        => AmplitudeCarrierRouting.ServesApiReads(Settings)
            ? Pending<T>(operation)
            : IntegrationResult.Technical<T>(
                IntegrationErrors.CapabilityNotSupported,
                $"This Amplitude installation does not serve {operation} live: its configured "
                + "release exposes no API service, so the figure comes from the INT-21 snapshot, "
                + "which reports it as stale.");

    /// <summary>
    /// <see cref="ErrorFamily.Technical"/> and never <see cref="ErrorFamily.Transient"/>. The
    /// family decides what the platform does next: <c>Technical</c> parks the command and alerts an
    /// administrator, which is right, while <c>Transient</c> would have the dispatcher retry a
    /// missing document with exponential backoff forever.
    ///
    /// <para>
    /// The release is read from the bound connection so the detail names the artefact this
    /// installation needs. Unreadable settings name both asks rather than guessing one — see
    /// <see cref="AmplitudeSpecification.MissingDocumentFor"/>.
    /// </para>
    /// </summary>
    private IntegrationResult Pending(string operation)
        => IntegrationResult.Technical(
            IntegrationErrors.AdapterSpecificationPending,
            AmplitudeSpecification.RefusalDetail(operation, Settings?.AmplitudeVersion));

    /// <inheritdoc cref="Pending(string)"/>
    private IntegrationResult<T> Pending<T>(string operation)
        => IntegrationResult.Technical<T>(
            IntegrationErrors.AdapterSpecificationPending,
            AmplitudeSpecification.RefusalDetail(operation, Settings?.AmplitudeVersion));
}
