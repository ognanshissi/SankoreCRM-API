namespace Sankore.Modules.Integration.Adapters.Sab;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Sankore.Modules.Integration.Domain;
using Sankore.Modules.Integration.Infrastructure;
using Sankore.Modules.Integration.Ports;
using Sankore.Modules.Integration.PublicApi;
using Sankore.Shared.Kernel;

/// <summary>
/// SAB AT, through the Open SAB API (INT-32).
///
/// ───────────────────────────────────────────────────────────────────────────────────────────────
/// <b>WHAT IS MISSING.</b> The Open SAB API catalogue. No document in this repository defines
/// which services the API exposes, their paths or verbs, the field names of a single request or
/// response, or the header the API key travels in. INT-32's own last acceptance criterion says so:
/// « Prérequis : catalogue d'API Open SAB, à négocier avec SBS en même temps qu'Amplitude », and
/// <see cref="SabSpecification.PlanReference"/> records it next to the three other adapters in the
/// same state. There is no test environment either, so criterion 3 (« tests de contrat verts sur
/// l'environnement de test fourni par l'IMF ou par SBS ») cannot be met at all.
///
/// <b>WHAT IS DELIVERED ANYWAY.</b> Criterion 2 in full — the entity, which is the half of this
/// story that is a security property rather than a mapping (see <see cref="SabEntityScope"/>) —
/// and the half of criterion 1 that does not touch the wire: the API key is resolved from the M12
/// vault, per connection, and its absence is reported as its own fault
/// (<see cref="SabCredential"/>). Plus the capability matrix, the keyed registration, the ports,
/// the health answer and the test harness. What every port method does instead of building a
/// request is refuse.
///
/// <b>WHAT ARRIVES WITH THE CATALOGUE.</b> A generated client and a file of field mappings — not
/// an architecture. If SBS hands over an OpenAPI document, it is generated and not hand-written:
/// M02's biometry client is the precedent and the scar, hand-written wire records against a
/// document nobody had read, not one field name matching, every call mapping to null and every
/// KYC file stuck in <c>Verifying</c> against a service that answered perfectly.
///
/// <b>THE FOUR THINGS TO SETTLE WITH SBS FIRST</b>, in this order (the full wording is in
/// <see cref="SabSpecification.OpenQuestions"/>, so that a procurement conversation and this class
/// quote the same list):
/// <list type="number">
/// <item>the service catalogue and its field names — nothing can be built without it;</item>
/// <item>how the API key and the <c>Entity</c> travel, and whether a key is bound to one entity —
///   this is what decides whether a mis-scoped call is detectable;</item>
/// <item>which reads Open SAB answers, and how it paginates them;</item>
/// <item>a test environment carrying at least TWO entities, without which criterion 3's green
///   suite would prove nothing about the scoping.</item>
/// </list>
/// ───────────────────────────────────────────────────────────────────────────────────────────────
///
/// <para>
/// <b>Why a refusal and not a plausible mapping.</b> Besides the biometry precedent, this adapter
/// has a failure mode of its own that no other blocked adapter has: a guessed Open SAB request is
/// a request whose entity scoping is also guessed, so the quiet outcome is not merely a write the
/// CBS never accepted — it is a write accepted by the WRONG INSTITUTION on a multi-IMF network,
/// which looks like success from here and is a data breach there. An explicit
/// <see cref="ErrorFamily.Technical"/> refusal is the honest state, and <c>Technical</c>
/// specifically: the dispatcher never retries it (retrying cannot obtain a catalogue) and alerts
/// the administrator instead.
/// </para>
///
/// <para>
/// <b>The order of the refusals is the order of the real call path</b>, and it is deliberate:
/// scope the call (entity), authenticate the call (API key), build the call (catalogue). Each step
/// names a different owner — the settings screen, the secret endpoint, SBS — so an operator is
/// sent to the one place that can move their problem forward. Putting the entity first is not a
/// cosmetic choice about messages: the guarantee has to be structural, because the day refusals
/// start being removed one method at a time, a method that calls before the scope check is a
/// method that calls Open SAB with no entity.
/// </para>
///
/// <para>
/// <b>What it does NOT claim.</b> <c>ICbsKycLevelPort</c> is not implemented. Whether Open SAB
/// exposes a customer's tier as a readable field is question 3, and the port's contract requires
/// an implementer to declare <see cref="IntegrationCapability.ReadKycLevel"/> — so claiming it
/// would have INT-21 compute a compliance divergence against a figure that cannot be obtained.
/// The query ports that ARE claimed (<c>ICbsTransactionPort</c> and the read methods of the
/// others) are claimed because Open SAB is an HTTP API in front of a full ledger and can answer a
/// query — unlike Perfect Vision, where not claiming them says "never". Here the same silence
/// would say "never" about something the catalogue is likely to offer, which would be the
/// opposite kind of lie.
/// </para>
///
/// <para>
/// <b>No call journal.</b> Every other adapter wraps its calls in <c>ICallJournal</c> (INT-08).
/// This one makes no outbound call, and a journal row for a call that never touched a back-office
/// would read as a failure of SAB rather than as a missing document on our side. The refusal is
/// returned to the dispatcher, which records it on the command where an administrator is already
/// looking.
/// </para>
/// </summary>
internal sealed class SabAdapter(
    IntegrationDbContext db,
    ITenantContext tenant,
    ISecretsModule secrets,
    TimeProvider clock,
    ILogger<SabAdapter> logger)
    : ICbsAdapter, ICbsCustomerPort, ICbsAccountPort, ICbsTransactionPort, ICbsLoanPort
{
    private IntegrationResult<SabBinding>? _binding;

    public IntegrationKind Kind => IntegrationKind.Sab;

    /// <summary>
    /// The matrix of <see cref="SabCapabilityMatrix"/>, applied to <b>the connection asked
    /// about</b> — the ten live operations when that installation names an entity, nothing at all
    /// when it does not.
    ///
    /// <para>
    /// Reads its settings straight off <paramref name="connection"/> and queries nothing, where
    /// before L8 it went through <see cref="Bind"/> — a lazy read that re-discovered "the tenant's
    /// SAB connection" by kind because the contract's property carried no connection. The binding
    /// survives for the port methods and the vault key, which have no row to hand.
    /// </para>
    /// </summary>
    public IntegrationCapabilities CapabilitiesFor(IntegrationConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);

        // `as`, not a cast: a SAB row carrying another kind's settings narrows to no matrix rather
        // than throwing out of a screen that is only asking what is available.
        return SabCapabilityMatrix.For(connection.Settings as SabSettings);
    }

    // ── Binding ─────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The tenant's SAB connection, its id and its settings — resolved once per scope.
    ///
    /// <para>
    /// <b>No longer read for the capability matrix.</b> That was its original reason —
    /// <c>ICbsAdapter.Capabilities</c> was a parameterless property and the matrix depends on the
    /// row — and <c>CapabilitiesFor</c> now takes its connection and reads the settings off it.
    /// </para>
    ///
    /// <para>
    /// It survives for the PORT methods and for the vault key below, neither of which is handed a
    /// connection. Read synchronously and lazily, cached for the scope: blocking on an async query
    /// with <c>GetAwaiter().GetResult()</c> is the deadlock-prone form of the same thing, and
    /// reading in the constructor would query for every resolution of the adapter including the
    /// ones that never make a call.
    /// </para>
    ///
    /// <para>
    /// This picks the tenant's first active SAB row while the matrix now speaks for whichever row
    /// it was handed. The two cannot differ for a core-banking kind:
    /// <c>ux_integration_connection_active_core_banking</c> allows one active row per tenant, and
    /// that index is precisely why this shape is safe here and was not safe for insurance.
    /// </para>
    ///
    /// <para>
    /// The connection ID is part of the binding and not an afterthought: it is half of the vault
    /// key the API key is stored under (<see cref="SabCredential"/>), and a credential looked up
    /// per tenant instead of per connection is a credential shared with this tenant's insurance
    /// connections.
    /// </para>
    ///
    /// <para>
    /// <b>Activation is NOT part of the predicate</b>, unlike <c>TemenosAdapter.BindAsync</c>. A
    /// SAB connection can never be activated — its health check cannot pass until the catalogue
    /// arrives — so filtering on <c>IsActive</c> would make the matrix permanently empty and the
    /// deliverable half of this chantier unobservable. Reading an inactive row grants nothing:
    /// every port method refuses regardless. An active row still wins where a tenant has several,
    /// so the day activation becomes possible this reads the connection commands actually flow
    /// through.
    /// </para>
    ///
    /// <para>
    /// <c>IgnoreQueryFilters</c> paired with an explicit tenant predicate, as every background
    /// path in this module does: the adapter is reached from the Hangfire dispatcher, where the
    /// ambient tenant comes from <c>BackgroundJobContext.SetScope</c>, and neither guard is
    /// load-bearing alone.
    /// </para>
    ///
    /// <para>
    /// The three failures are told apart because they send an administrator to three different
    /// screens: no tenant on the scope is a composition fault, no SAB connection is INT-03's
    /// configuration screen, and settings of the wrong shape is a row that must be corrected.
    /// </para>
    /// </summary>
    private IntegrationResult<SabBinding> Bind()
    {
        if (_binding is not null) return _binding;

        if (!tenant.HasTenant || tenant.CurrentTenantId == Guid.Empty)
        {
            return _binding = IntegrationResult.Technical<SabBinding>(
                IntegrationErrors.NoActiveConnection,
                "No tenant is established on this scope, so no SAB connection can be resolved.");
        }

        var tenantId = tenant.CurrentTenantId;

        var connection = db.Connections
            .IgnoreQueryFilters()
            .Where(c => c.TenantId == tenantId && c.Kind == IntegrationKind.Sab)
            .OrderByDescending(c => c.IsActive)
            .ThenBy(c => c.CreatedAt)
            .FirstOrDefault();

        if (connection is null)
        {
            return _binding = IntegrationResult.Technical<SabBinding>(
                IntegrationErrors.NoActiveConnection,
                "This tenant has no SAB core banking connection.");
        }

        if (connection.Settings is not SabSettings settings)
        {
            // A SAB row carrying another kind's settings is a corrupted configuration, not a
            // missing catalogue: the two send an administrator to different screens, so they are
            // told apart even though both end in no matrix.
            logger.LogError(
                "Connection {ConnectionId} is a SAB row whose settings are not SabSettings; its "
                + "capability matrix will be empty and every call will refuse.",
                connection.Id);

            return _binding = IntegrationResult.Technical<SabBinding>(
                IntegrationErrors.SettingsInvalid,
                "This SAB connection does not carry SAB settings.");
        }

        return _binding = IntegrationResult.Ok(
            new SabBinding(connection.TenantId, connection.Id, settings));
    }

    // ── Health (INT-03) ─────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Answers, and answers honestly: <b>unhealthy</b>, in every branch, because the integration
    /// cannot operate.
    ///
    /// <para>
    /// No exception, by design. This is called from the activation screen and from the
    /// <c>integration</c> health check, and a throwing adapter would turn a known supplier
    /// dependency into a 500 that reads like an outage of ours.
    /// </para>
    ///
    /// <para>
    /// <b>It also cannot be made to pass, and that is the point.</b>
    /// <c>IntegrationConnection.Activate</c> refuses without a successful check, so a SAB
    /// connection stays inactive — and every queuing path goes through
    /// <c>RequireCoreBankingAsync</c>, which resolves by family AND <c>IsActive</c>. Two
    /// independent locks, and the SAB stakes are sharper than Perfect Vision's: an active
    /// connection here would have the dispatcher reach ports with no mapping behind them, and it
    /// would also occupy the tenant's SINGLE active core-banking slot
    /// (<c>ux_integration_connection_active_core_banking</c>) — so an unmappable SAB row could
    /// displace the connection that actually works.
    /// </para>
    ///
    /// <para>
    /// The branches are told apart even though they all end in <c>Unhealthy</c>, because they send
    /// different people to different screens: settings of the wrong shape and a missing entity are
    /// the tenant's own configuration, a missing API key is one vault write, and only the last one
    /// is a conversation with SBS. The entity comes before the credential for the reason stated on
    /// the class: scope first, always.
    /// </para>
    ///
    /// <para>
    /// No latency is reported, since nothing was called. Reporting zero would put a SAB row in the
    /// activation screen's latency column next to systems that actually answered.
    /// </para>
    /// </summary>
    public async Task<IntegrationHealth> CheckHealthAsync(
        IntegrationConnection connection, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(connection);

        var checkedAt = clock.GetUtcNow();

        if (connection.Settings is not SabSettings settings)
        {
            return IntegrationHealth.Unhealthy(
                $"{IntegrationErrors.SettingsInvalid}: this SAB connection does not carry SAB "
                + "settings.",
                checkedAt);
        }

        if (SabEntityScope.ResolveFor(settings) != SabEntityResolution.Resolved)
        {
            return IntegrationHealth.Unhealthy(
                $"{IntegrationErrors.SettingsInvalid}: {SabEntityScope.MissingDetail()}", checkedAt);
        }

        // Read from the connection we were handed and not from the binding: the health check runs
        // BEFORE a connection is active, and on a row the caller may have just created, so there
        // is nothing for Bind() to find.
        var credential = await SabCredential.ProbeAsync(secrets, connection.TenantId, connection.Id, ct);

        if (credential == SabCredentialState.Missing)
        {
            return IntegrationHealth.Unhealthy(
                $"{IntegrationErrors.CredentialMissing}: {SabCredential.MissingDetail()}", checkedAt);
        }

        return IntegrationHealth.Unhealthy(
            $"{IntegrationErrors.AdapterSpecificationPending}: {SabSpecification.HealthDetail()}",
            checkedAt);
    }

    // ── ICbsCustomerPort (INT-12) ───────────────────────────────────────────────────────────

    /// <summary>
    /// Refuses, naming whichever of the three things is missing.
    ///
    /// <para>
    /// A customer record is the first thing an Open SAB call would carry and the one whose field
    /// list is most completely unknown: <c>CbsCustomerPayload</c> holds twenty fields, several of
    /// them coded (<c>IdDocumentType</c>, <c>Gender</c>, <c>AgencyCode</c>) and needing translation
    /// through <c>integration_mapping</c> — and a mapping table cannot be filled in without the
    /// target vocabulary either.
    /// </para>
    /// </summary>
    public Task<IntegrationResult<ExternalId>> CreateCustomerAsync(
        CbsCustomerPayload payload, IdempotencyKey key, CancellationToken ct)
        => RefuseAsync<ExternalId>(SabOperations.CreateCustomer, ct);

    /// <inheritdoc cref="CreateCustomerAsync"/>
    public Task<IntegrationResult> UpdateCustomerAsync(
        ExternalId id, CbsCustomerPayload payload, IdempotencyKey key, CancellationToken ct)
        => RefuseAsync(SabOperations.UpdateCustomer, ct);

    /// <inheritdoc cref="CreateCustomerAsync"/>
    public Task<IntegrationResult> SetKycLevelAsync(
        ExternalId id, KycLevel level, IdempotencyKey key, CancellationToken ct)
        => RefuseAsync(SabOperations.SetKycLevel, ct);

    // ── ICbsAccountPort (INT-13, INT-15, ASS-05) ────────────────────────────────────────────

    /// <inheritdoc cref="CreateCustomerAsync"/>
    public Task<IntegrationResult<ExternalId>> OpenAccountAsync(
        ExternalId customerId, string productCode, IdempotencyKey key, CancellationToken ct)
        => RefuseAsync<ExternalId>(SabOperations.OpenAccount, ct);

    /// <inheritdoc cref="CreateCustomerAsync"/>
    public Task<IntegrationResult<IReadOnlyList<CbsAccount>>> GetAccountsAsync(
        ExternalId customerId, CancellationToken ct)
        => RefuseAsync<IReadOnlyList<CbsAccount>>(SabOperations.ReadAccounts, ct);

    /// <summary>
    /// Refuses. Declared <see cref="CapabilityMode.RealTime"/> in the matrix, so
    /// <c>IntegrationModuleFacade</c> would attempt the live read, receive this, log it and fall
    /// back to the INT-21 snapshot with <c>IsStale</c> set — degraded, labelled, and never a wrong
    /// figure.
    ///
    /// <para>
    /// A balance is read at a counter, in front of a client, which is why guessing here would be
    /// the worst place to guess: a field guessed right in name but wrong in meaning — ledger
    /// balance where the available balance was wanted — answers confidently with a number that is
    /// simply not the one the clerk needs. And on a CIF-style network a balance read with the
    /// wrong entity is another institution's account.
    /// </para>
    /// </summary>
    public Task<IntegrationResult<CbsBalance>> GetBalanceAsync(
        ExternalId accountId, CancellationToken ct)
        => RefuseAsync<CbsBalance>(SabOperations.ReadBalance, ct);

    /// <summary>
    /// Not supported, and not pending: the premium debit of ASS-05 is outside INT-32's criteria,
    /// so it is absent from the capability matrix and
    /// <c>IntegrationAdapterResolver.ResolvePort</c> refuses the call before it reaches here. The
    /// method exists because <c>ICbsAccountPort</c> demands it — the same position
    /// <c>TemenosAdapter</c> takes.
    ///
    /// <para>
    /// It answers <see cref="IntegrationErrors.CapabilityNotSupported"/> rather than the catalogue
    /// refusal because nothing is being waited on: this adapter would not debit an account even
    /// with the catalogue in hand, until ASS-05 brings it into scope. It is also the one pair of
    /// methods that does NOT run the entity guard, deliberately — the guard protects calls that
    /// will one day be made, and an operation this adapter never makes needs no scope. The
    /// alternative would tell an operator asking for an out-of-scope operation to go and configure
    /// a field that would change nothing.
    /// </para>
    /// </summary>
    public Task<IntegrationResult<CbsDebitReceipt>> DebitAccountAsync(
        ExternalId accountId, decimal amount, string currency, string label,
        IdempotencyKey key, CancellationToken ct)
        => Task.FromResult(OutOfScope(SabOperations.DebitAccount));

    /// <inheritdoc cref="DebitAccountAsync"/>
    public Task<IntegrationResult<CbsDebitReceipt>> ReverseDebitAsync(
        ExternalId accountId, string originalReference, IdempotencyKey key, CancellationToken ct)
        => Task.FromResult(OutOfScope(SabOperations.ReverseDebit));

    // ── ICbsTransactionPort (INT-13, INT-22) ────────────────────────────────────────────────

    /// <summary>
    /// Refuses. Claimed as an interface because an HTTP core banking API can answer a query — the
    /// open question is how Open SAB paginates it, which is question 3 and the reason no cursor
    /// contract is invented here.
    /// </summary>
    public Task<IntegrationResult<CbsPage<CbsTransaction>>> GetTransactionsAsync(
        ExternalId accountId, DateOnly from, DateOnly to, string? cursor, CancellationToken ct)
        => RefuseAsync<CbsPage<CbsTransaction>>(SabOperations.ReadTransactions, ct);

    /// <inheritdoc cref="GetTransactionsAsync"/>
    public Task<IntegrationResult<CbsMonthlyFlow>> GetMonthlyFlowAsync(
        ExternalId customerId, YearMonth month, CancellationToken ct)
        => RefuseAsync<CbsMonthlyFlow>(SabOperations.ReadMonthlyFlow, ct);

    // ── ICbsLoanPort ────────────────────────────────────────────────────────────────────────

    /// <inheritdoc cref="CreateCustomerAsync"/>
    public Task<IntegrationResult<ExternalId>> SubmitLoanApplicationAsync(
        CbsLoanApplicationPayload payload, IdempotencyKey key, CancellationToken ct)
        => RefuseAsync<ExternalId>(SabOperations.SubmitLoanApplication, ct);

    /// <inheritdoc cref="GetTransactionsAsync"/>
    public Task<IntegrationResult<IReadOnlyList<CbsLoan>>> GetLoansAsync(
        ExternalId customerId, CancellationToken ct)
        => RefuseAsync<IReadOnlyList<CbsLoan>>(SabOperations.ReadLoans, ct);

    // ── The refusal chain, in one place ─────────────────────────────────────────────────────

    /// <summary>
    /// Scope, authenticate, build — and stop at the first one that cannot be satisfied.
    ///
    /// <para>
    /// One method for the whole adapter, so that no port method can be written that skips a step.
    /// That is the entire guarantee of criterion 2 at call time: the entity check is not a line
    /// somebody remembered to put in <c>CreateCustomerAsync</c>, it is the only path to a result.
    /// </para>
    /// </summary>
    private async Task<SabRefusal> RefusalAsync(string operation, CancellationToken ct)
    {
        var bound = Bind();

        // No connection, no tenant, or settings of the wrong shape. Forwarded as-is: each already
        // names the screen that owns it.
        if (bound.IsFailure) return new SabRefusal(bound.Code!, bound.Detail!);

        var binding = bound.Value;

        // 1. SCOPE. First, always, and fail-closed — see SabEntityScope for why the direction is
        //    not a matter of taste on a multi-IMF network.
        if (SabEntityScope.ResolveFor(binding.Settings) != SabEntityResolution.Resolved)
            return new SabRefusal(IntegrationErrors.SettingsInvalid, SabEntityScope.MissingDetail());

        // 2. AUTHENTICATE. Presence only, and from the vault — never the value, which this process
        //    has no use for until the catalogue says how it travels.
        var credential = await SabCredential.ProbeAsync(secrets, binding.TenantId, binding.ConnectionId, ct);

        if (credential == SabCredentialState.Missing)
            return new SabRefusal(IntegrationErrors.CredentialMissing, SabCredential.MissingDetail());

        // 3. BUILD. Everything our side owns is in place; what is missing is the vendor document.
        return new SabRefusal(
            IntegrationErrors.AdapterSpecificationPending, SabSpecification.RefusalDetail(operation));
    }

    /// <summary>
    /// <see cref="ErrorFamily.Technical"/> for every branch, and never
    /// <see cref="ErrorFamily.Transient"/>. The family decides what the platform does next:
    /// <c>Technical</c> parks the command and alerts an administrator, which is right for all
    /// three causes, while <c>Transient</c> would have the dispatcher retry a missing entity, a
    /// missing key and a missing document with exponential backoff forever.
    /// </summary>
    private async Task<IntegrationResult> RefuseAsync(string operation, CancellationToken ct)
    {
        var refusal = await RefusalAsync(operation, ct);

        return IntegrationResult.Technical(refusal.Code, refusal.Detail);
    }

    /// <inheritdoc cref="RefuseAsync(string, CancellationToken)"/>
    private async Task<IntegrationResult<T>> RefuseAsync<T>(string operation, CancellationToken ct)
    {
        var refusal = await RefusalAsync(operation, ct);

        return IntegrationResult.Technical<T>(refusal.Code, refusal.Detail);
    }

    /// <summary>The ASS-05 answer: this adapter does not do it, and is not waiting to.</summary>
    private static IntegrationResult<CbsDebitReceipt> OutOfScope(string operation)
        => IntegrationResult.Technical<CbsDebitReceipt>(
            IntegrationErrors.CapabilityNotSupported,
            $"SAB AT does not serve {operation} through this adapter: it belongs to ASS-05 and is "
            + "not declared in the capability matrix. This is not the missing Open SAB catalogue.");

    /// <summary>A code and a detail, so one chain can serve both result shapes.</summary>
    private readonly record struct SabRefusal(string Code, string Detail);

    /// <summary>
    /// Which installation a call is about: the tenant, the connection id the API key is stored
    /// under, and the settings already narrowed to the right type.
    ///
    /// <para>
    /// It exists because the ports take no connection — <c>CreateCustomerAsync</c> receives a
    /// payload and an idempotency key and nothing else, deliberately, so that a consumer module
    /// never has to know a connection exists. <c>Settings</c> is non-null by construction: a row
    /// whose settings are not <c>SabSettings</c> never produces a binding, it produces a technical
    /// failure naming the problem.
    /// </para>
    /// </summary>
    private sealed record SabBinding(Guid TenantId, Guid ConnectionId, SabSettings Settings);
}
