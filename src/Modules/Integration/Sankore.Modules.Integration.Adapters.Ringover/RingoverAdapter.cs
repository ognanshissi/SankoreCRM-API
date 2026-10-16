namespace Sankore.Modules.Integration.Adapters.Ringover;

using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Sankore.Modules.Integration.Domain;
using Sankore.Modules.Integration.Infrastructure;
using Sankore.Modules.Integration.Ports;
using Sankore.Modules.Integration.PublicApi;
using Sankore.Shared.Kernel;

/// <summary>
/// Ringover — the first real telephony provider (TEL-B-04).
///
/// ───────────────────────────────────────────────────────────────────────────────────────────────
/// <b>WHAT IS MISSING.</b> A Ringover test account, and with it the answers to the six open
/// points. No document in this repository defines a call status the provider reports, the body it
/// returns when a number is refused, or how it authenticates a webhook.
/// <see cref="RingoverSpecification.PlanReference"/> records the block; the plan states the
/// condition outright — lot 7 « est conditionné au compte de test <b>et</b> aux six points ouverts
/// du §7 ».
///
/// <b>WHICH MAKES ONE CRITERION UNDELIVERABLE AND LEAVES THE REST DELIVERED.</b> Undeliverable:
/// the contract suite of TEL-B-21 run green against the real account, which by definition needs
/// the account. Delivered in full, here: the status-translation RULE
/// (<see cref="RingoverStatusTranslation"/>), the error classification
/// (<see cref="RingoverErrorClassification"/>), the capability matrix
/// (<see cref="RingoverCapabilityMatrix"/>), the two vault secrets
/// (<see cref="RingoverCredential"/>), the webhook parser below, and the three spec fallbacks —
/// which are not stubs waiting here but the production behaviour of lots 2, 3 and 5
/// (<see cref="RingoverFallbacks"/>).
///
/// <b>WHAT ARRIVES WITH THE ACCOUNT.</b> A table of status tokens, a set of error tokens, and an
/// HTTP client — not an architecture. If Ringover publishes an OpenAPI document, that client is
/// GENERATED and not hand-written: M02's biometry client is the precedent and the scar —
/// hand-written wire records against a document nobody had read, not one field name matching,
/// every call mapping to null, and every KYC file stuck in <c>Verifying</c> against a service that
/// answered perfectly.
///
/// <b>THE QUESTIONS TO SETTLE FIRST</b>, in the order they unblock work, are in
/// <see cref="RingoverSpecification.OpenQuestions"/> — so that a procurement conversation and this
/// class quote the same list. The structural point about them: <b>only questions 1 to 3 block
/// anything.</b> Questions 4, 5 and 6 each have a fallback that is already built and tested, so
/// their answers can only replace a working mechanism with a cheaper or more exact one. The
/// opposite reading — "six open points, so six things do not work" — is wrong, and would have
/// somebody rebuild what exists.
/// ───────────────────────────────────────────────────────────────────────────────────────────────
///
/// <para>
/// <b>Why a refusal and not a plausible implementation, and why a status is the worst thing in
/// this repository to guess.</b> A guessed endpoint answers 404 and the failure is seen. A guessed
/// STATUS succeeds: the journal accepts it, the call row changes, and nothing ever reports it. Two
/// consequences, both real. A conversation recorded as missed puts a client back on an agent's
/// call-back list, and somebody rings them to apologise for a call that already happened. A missed
/// call recorded as completed takes that client OFF the list and stops the lead's first-contact
/// clock (TEL-B-12) — so the SLA reports that the branch responded, and the prospect is never
/// called at all. An explicit <see cref="ErrorFamily.Technical"/> refusal is the honest state, and
/// <c>Technical</c> specifically: retrying cannot obtain an account, so the platform parks the
/// command and alerts the administrator instead of hammering the provider.
/// </para>
///
/// <para>
/// <b>WHAT IT CLAIMS, AND WHAT IT DOES NOT.</b> <c>ITelephonyPort</c> is implemented, and one of
/// its eight members genuinely WORKS: <see cref="ParseWebhook"/>. That is not an inconsistency —
/// it is the contract. The parser is specified as pure, and what it cannot determine locally it is
/// told to leave null « for the handler to resolve by re-reading the call ». Without a vocabulary
/// there is nothing it CAN determine locally except a deduplication key, and a hash of the body is
/// a correct one rather than a placeholder: it makes an identical redelivery idempotent, which is
/// exactly what <c>tel_webhook_event</c> needs, and it needs no field name to compute. So the one
/// member that can be finished without the account is finished.
/// </para>
///
/// <para>
/// <b>Registered as <c>ICbsAdapter</c> even though it serves the telephony family</b>, because
/// that is how this module resolves any port: there is no <c>ITelephonyAdapter</c>.
/// <c>IntegrationAdapterResolver.ResolveAdapter</c> looks up a keyed <c>ICbsAdapter</c> by
/// <c>connection.Kind</c> for every family, and <c>ResolvePort</c> casts to the port it wants.
/// The interface's name is a legacy of the core-banking-first design, not a statement about the
/// family; what it carries — a kind, a capability matrix and a health check — is family-neutral,
/// and the insurance family already does the same.
/// </para>
///
/// <para>
/// <b>No call journal.</b> Every operating adapter wraps its calls in <c>ICallJournal</c>
/// (INT-08). This one makes no outbound call, and a journal row for a call that never touched the
/// provider would read as a failure of Ringover rather than as a missing account on our side. The
/// refusal is returned to the caller, which records it where somebody is already looking.
/// </para>
/// </summary>
internal sealed class RingoverAdapter(
    IntegrationDbContext db,
    ITenantContext tenant,
    ISecretsModule secrets,
    TimeProvider clock,
    ILogger<RingoverAdapter> logger)
    : ICbsAdapter, ITelephonyPort
{
    private IntegrationResult<RingoverBinding>? _binding;

    public IntegrationKind Kind => IntegrationKind.Ringover;

    /// <summary>
    /// The matrix of <see cref="RingoverCapabilityMatrix"/>, applied to the connection asked
    /// about. Reads its input off the row and queries nothing.
    /// </summary>
    public IntegrationCapabilities CapabilitiesFor(IntegrationConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);

        // `as`, not a cast: a Ringover row carrying another kind's settings narrows to no matrix
        // rather than throwing out of a screen that is only asking what is available.
        return RingoverCapabilityMatrix.For(connection.Settings as RingoverSettings);
    }

    /// <summary>
    /// Answers unhealthy, naming whichever of the three things is missing — and in that order,
    /// because they send an administrator to three different places.
    ///
    /// <para>
    /// Reads from the connection it was handed and not from the binding: a health check runs
    /// BEFORE a connection is active, on a row the caller may have just created, so there is
    /// nothing for <see cref="Bind"/> to find.
    /// </para>
    ///
    /// <para>
    /// <b>A Ringover connection can therefore never be activated</b> — <c>ActivateConnectionHandler</c>
    /// requires a passing health check — and that is the intended state. The telephony family
    /// works end to end against the in-memory adapter, which is what every lot before this one was
    /// built and verified on.
    /// </para>
    /// </summary>
    public async Task<IntegrationHealth> CheckHealthAsync(
        IntegrationConnection connection, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(connection);

        var checkedAt = clock.GetUtcNow();

        if (connection.Settings is not RingoverSettings settings)
        {
            return IntegrationHealth.Unhealthy(
                $"{IntegrationErrors.SettingsInvalid}: this Ringover connection does not carry "
                + "Ringover settings.",
                checkedAt);
        }

        if (string.IsNullOrWhiteSpace(settings.ApiHost))
        {
            return IntegrationHealth.Unhealthy(
                $"{IntegrationErrors.SettingsInvalid}: this Ringover connection names no API "
                + "host. The provider serves regions on distinct hosts, so there is no default "
                + "that would be right for every tenant — and a tenant whose data must stay in "
                + "one region cannot be pointed at another.",
                checkedAt);
        }

        var credential = await RingoverCredential.ProbeAsync(
            secrets, connection.TenantId, connection.Id, ct);

        if (credential != RingoverCredentialState.Present)
        {
            return IntegrationHealth.Unhealthy(
                $"{IntegrationErrors.CredentialMissing}: "
                + RingoverCredential.MissingDetail(credential),
                checkedAt);
        }

        return IntegrationHealth.Unhealthy(
            $"{IntegrationErrors.AdapterSpecificationPending}: "
            + RingoverSpecification.HealthDetail(),
            checkedAt);
    }

    // ── ITelephonyPort (TEL-B-02) ───────────────────────────────────────────────────────────

    /// <summary>
    /// Refuses, naming whichever of the three things is missing.
    ///
    /// <para>
    /// The directory is the operation whose absence is felt first: without it nobody can be
    /// linked (TEL-B-05), so every call that did arrive would be journalled unattributed. It is
    /// also the operation whose shape is LEAST unknown — a list of users with ids, e-mails and
    /// numbers — which is precisely why it must still refuse: a plausible list is a list of
    /// accounts that do not exist, and an administrator would link real agents to them.
    /// </para>
    /// </summary>
    public Task<IntegrationResult<IReadOnlyList<ProviderUser>>> ListUsersAsync(CancellationToken ct)
        => RefuseAsync<IReadOnlyList<ProviderUser>>(RingoverOperations.ListUsers, ct);

    /// <summary>
    /// Refuses — and this is the refusal an AGENT sees, not an operator.
    ///
    /// <para>
    /// The click-to-call slice calls the provider synchronously, with no queue and no retry,
    /// because the agent is waiting for their telephone to ring. So this refusal reaches a screen
    /// in front of somebody mid-conversation with a client, and <c>RequestClickToCallHandler</c>
    /// writes a <c>Failed</c> request row outside the ambient transaction for exactly that reason:
    /// the refusal is recorded even though the command fails.
    /// </para>
    /// </summary>
    public Task<IntegrationResult<ExternalId>> StartCallbackAsync(
        ExternalId providerUserId, string targetNumber, string? callerNumber, CancellationToken ct)
        => RefuseAsync<ExternalId>(RingoverOperations.StartCallback, ct);

    /// <summary>
    /// Refuses. <b>This is the method the status table is for</b>: the journal re-reads every call
    /// through it, because a webhook says something happened and this says what. The translation
    /// rule is built and tested (<see cref="RingoverStatusTranslation"/>); what is missing is the
    /// vocabulary it would translate.
    /// </summary>
    public Task<IntegrationResult<ProviderCall>> GetCallAsync(
        ExternalId providerCallId, CancellationToken ct)
        => RefuseAsync<ProviderCall>(RingoverOperations.GetCall, ct);

    /// <inheritdoc cref="GetCallAsync"/>
    public Task<IntegrationResult<ProviderCallPage>> ListCallsAsync(
        DateTimeOffset from, string? cursor, CancellationToken ct)
        => RefuseAsync<ProviderCallPage>(RingoverOperations.ListCalls, ct);

    /// <summary>
    /// Refuses. Declared in the matrix, so the refusal is "not yet" and not "never" — and the
    /// fetch job is harmless meanwhile, because TEL-B-15's policy ships <c>Disabled</c> and the
    /// job skips in a line.
    /// </summary>
    public Task<IntegrationResult<ProviderRecording>> DownloadRecordingAsync(
        ExternalId providerCallId, CancellationToken ct)
        => RefuseAsync<ProviderRecording>(RingoverOperations.DownloadRecording, ct);

    /// <summary>
    /// Refuses, and <b>differently from the others</b>: this one is not waiting for the account.
    ///
    /// <para>
    /// <see cref="IntegrationCapability.RecordingDeletion"/> is not declared in the matrix, so the
    /// honest answer is <see cref="IntegrationErrors.CapabilityNotSupported"/> — nothing calls
    /// this, and the purge job never asks. Reporting the pending specification here would promise
    /// that the provider's copy will be destroyed one day, which is open question 6 and not a
    /// certainty; <see cref="RingoverFallbacks.RecordingDeletion"/> states the consequence in a
    /// sentence somebody can forward to a regulator.
    /// </para>
    /// </summary>
    public Task<IntegrationResult> DeleteRecordingAsync(
        ExternalId providerCallId, CancellationToken ct)
        => Task.FromResult(IntegrationResult.Technical(
            IntegrationErrors.CapabilityNotSupported,
            "Ringover does not serve DeleteRecording through this adapter: RecordingDeletion is "
            + "not declared in the capability matrix, so SANKORE's retention purge never asks the "
            + "provider. This is NOT the missing test account — it is the spec's own fallback, "
            + "« purge côté SANKORE seule », applied by default. The consequence: a recording "
            + "destroyed here still exists at the provider, so the retention is a statement about "
            + "SANKORE and not about the conversation. See open question 6."));

    /// <summary>
    /// <b>Implemented, and the only member here that is.</b> Reduces a webhook body to the one
    /// thing that can be established without the provider's vocabulary: a deduplication key.
    ///
    /// <para>
    /// <b>Why this is a finished implementation and not a stub.</b> The contract specifies the
    /// parser as pure and tells an adapter to leave null whatever it cannot determine locally,
    /// « for the handler to resolve by re-reading the call » — which the journal does
    /// unconditionally, for every event, whatever it said. The one member with no fallback is the
    /// key, and the contract's own guidance is to compose it « from the call, the type and the
    /// instant ». With no field names, a cryptographic hash of the exact bytes is the same idea
    /// reached from the only input available, and it is strictly correct for the purpose: two
    /// deliveries of the same body produce the same key, which is what <c>tel_webhook_event</c>
    /// needs to make a replay a no-op. Two DIFFERENT events never collide.
    /// </para>
    ///
    /// <para>
    /// <b>What it does not do, and must not.</b> It does not look for a call id, a status or a
    /// timestamp, because every one of those is a field name nobody has read. A parser that
    /// guessed and found nothing would answer null for all three anyway; a parser that guessed and
    /// found something would be worse, because a wrong status is the failure this whole assembly
    /// exists to avoid — see the class remarks. The handler re-reads the call, which refuses, and
    /// the event is recorded as received and unprocessable. That is the correct behaviour for a
    /// webhook from an account that does not exist.
    /// </para>
    ///
    /// <para>
    /// <b>No header is read either</b>, and that is deliberate rather than lazy: whether the
    /// provider signs its webhooks is open question 4, the verification happens upstream in
    /// <c>SyncWebhookVerifier</c> before this is ever called, and a parser that reached into
    /// headers on an unverified request would be reading attacker-controlled input to decide
    /// something.
    /// </para>
    /// </summary>
    public ProviderWebhookEvent ParseWebhook(TelephonyWebhookRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        // SHA-256 of the exact bytes, hex. Not a GUID: a fresh identifier per delivery would make
        // every redelivery a new event, which is precisely the duplicate tel_webhook_event exists
        // to stop. Not a timestamp either, for the same reason.
        var hash = Convert.ToHexStringLower(
            SHA256.HashData(Encoding.UTF8.GetBytes(request.Body ?? string.Empty)));

        // Prefixed, so a key is readable in a table and cannot be mistaken for the provider's own
        // identifier by somebody writing a query — and so the day the vocabulary arrives, the
        // rows written under this scheme are distinguishable from the ones written under the real
        // one.
        return new ProviderWebhookEvent(
            DedupKey: $"ringover-unparsed:{hash}",

            // A constant, not a guess at the provider's event names. It is what it is: a body
            // this module received and cannot interpret.
            EventType: "ringover.unparsed",

            // All three null, which the contract provides for: the handler resolves them by
            // re-reading the call.
            CallId: null,
            Status: null,
            OccurredAt: null);
    }

    // ── Binding and refusal ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// The tenant's Ringover connection, resolved once per scope.
    ///
    /// <para>
    /// <b>ONE row and not a list</b>, unlike the insurance adapters: the telephony family has its
    /// own partial unique index — one ACTIVE connection per tenant, the twin of
    /// <c>ux_integration_connection_active_core_banking</c> — so "the tenant's Ringover
    /// connection" is a well-formed phrase here in a way it is not for an insurer group with an
    /// IARD and a Vie undertaking. The row is picked deterministically all the same, so two reads
    /// agree.
    /// </para>
    ///
    /// <para>
    /// <b>Activation is NOT part of the predicate.</b> A Ringover connection can never be
    /// activated — its health check cannot pass until the account exists — so filtering on
    /// <c>IsActive</c> would make the binding permanently empty and the deliverable half of this
    /// lot unobservable. Reading an inactive row grants nothing: every port method refuses
    /// regardless.
    /// </para>
    ///
    /// <para>
    /// Read synchronously and lazily. The port methods carry no connection — by design, so a
    /// consumer module never has to know a connection exists — so a refusal has to find the row
    /// itself; blocking on an async query with <c>GetAwaiter().GetResult()</c> is the
    /// deadlock-prone shape of the same thing, and reading in the constructor would query on every
    /// resolution of the adapter, including the ones that never refuse anything.
    /// </para>
    ///
    /// <para>
    /// <c>IgnoreQueryFilters</c> paired with an explicit tenant predicate, as every background
    /// path in this module does: the adapter is reached from Hangfire jobs where the ambient
    /// tenant comes from <c>BackgroundJobContext.SetScope</c>, and neither guard is load-bearing
    /// alone.
    /// </para>
    /// </summary>
    private IntegrationResult<RingoverBinding> Bind()
    {
        if (_binding is not null) return _binding;

        var tenantId = tenant.CurrentTenantId;

        if (tenantId == Guid.Empty)
        {
            // No tenant on the scope at all: a composition fault, not a configuration one. It
            // sends a developer to the job or the request that resolved this adapter, which is a
            // different screen from the two below.
            return _binding = IntegrationResult.Technical<RingoverBinding>(
                // ConnectionNotFound and not a tenant-specific code: this module has none, and
                // inventing one would put a string in an operator's screen that no other
                // adapter uses for the same situation. The detail says which of the two it is.
                IntegrationErrors.ConnectionNotFound,
                "No tenant is resolved on this scope, so the Ringover connection cannot be "
                + "found. This is a composition fault and not a configuration one: a background "
                + "job must call BackgroundJobContext.SetScope BEFORE creating its service "
                + "scope.");
        }

        var row = db.Connections
            .IgnoreQueryFilters()
            .Where(c => c.TenantId == tenantId && c.Kind == IntegrationKind.Ringover)
            .OrderBy(c => c.Id)
            .FirstOrDefault();

        if (row is null)
        {
            return _binding = IntegrationResult.Technical<RingoverBinding>(
                IntegrationErrors.ConnectionNotFound,
                "This tenant has no Ringover connection. Create one through the integration "
                + "connection screen (INT-03).");
        }

        if (row.Settings is not RingoverSettings settings)
        {
            return _binding = IntegrationResult.Technical<RingoverBinding>(
                IntegrationErrors.SettingsInvalid,
                "This tenant's Ringover connection does not carry Ringover settings. The row has "
                + "to be corrected before anything else can be said about it.");
        }

        return _binding = IntegrationResult.Ok(
            new RingoverBinding(tenantId, row.Id, settings));
    }

    /// <summary>
    /// The three checks, in the order that sends an operator to the right place — <b>and that
    /// order is the useful part of this method.</b>
    ///
    /// <para>
    /// Settings first, then the credentials, then the account. The first two are the tenant's own
    /// screens and an afternoon's work; only the third is a procurement conversation. Answering
    /// "we are waiting for Ringover" to a connection that simply has no API key saved is how a
    /// deployment stays broken for a reason nobody owns.
    /// </para>
    ///
    /// <para>
    /// One chain, used by every refusing member, so that the refusal cannot differ between
    /// operations by accident.
    /// </para>
    /// </summary>
    private async Task<RingoverRefusal> RefusalAsync(string operation, CancellationToken ct)
    {
        var bound = Bind();

        // No tenant, no connection, or settings of the wrong shape. Forwarded as-is: each already
        // names the screen that owns it.
        if (bound.IsFailure) return new RingoverRefusal(bound.Code!, bound.Detail!);

        var binding = bound.Value;

        // 1. COORDINATES. The one setting a telephony connection cannot work without.
        if (string.IsNullOrWhiteSpace(binding.Settings.ApiHost))
        {
            return new RingoverRefusal(
                IntegrationErrors.SettingsInvalid,
                "This tenant's Ringover connection names no API host. The provider serves regions "
                + "on distinct hosts, so there is no default that would be right for every "
                + "tenant.");
        }

        // 2. AUTHENTICATE. Presence only, and from the vault — never the value, which this
        //    process has no use for until the vocabulary is known.
        var credential = await RingoverCredential.ProbeAsync(
            secrets, binding.TenantId, binding.ConnectionId, ct);

        if (credential != RingoverCredentialState.Present)
        {
            return new RingoverRefusal(
                IntegrationErrors.CredentialMissing,
                RingoverCredential.MissingDetail(credential));
        }

        // 3. CALL. Everything our side owns is in place; what is missing is the test account.
        //
        // Logged at warning and once per refusal: a configured Ringover connection reaching this
        // means somebody expects telephony to work in this deployment, and that expectation is
        // worth a line somebody will see — the alternative is an agent reporting that a button
        // does nothing while nothing in the logs says why.
        logger.LogWarning(
            "Ringover refused {Operation} for tenant {TenantId}: {Missing}",
            operation, binding.TenantId, RingoverSpecification.MissingPrerequisites);

        return new RingoverRefusal(
            IntegrationErrors.AdapterSpecificationPending,
            RingoverSpecification.RefusalDetail(operation));
    }

    /// <summary>
    /// <see cref="ErrorFamily.Technical"/> for every branch, and never
    /// <see cref="ErrorFamily.Transient"/>. The family decides what the platform does next:
    /// <c>Technical</c> parks the work and alerts an administrator, which is right for all three
    /// causes, while <c>Transient</c> would have the dispatcher retry a missing setting, a missing
    /// key and a missing account with exponential backoff forever.
    /// </summary>
    private async Task<IntegrationResult<T>> RefuseAsync<T>(string operation, CancellationToken ct)
    {
        var refusal = await RefusalAsync(operation, ct);

        return IntegrationResult.Technical<T>(refusal.Code, refusal.Detail);
    }

    /// <summary>A code and a detail, so one chain can serve both result shapes.</summary>
    private readonly record struct RingoverRefusal(string Code, string Detail);

    /// <summary>
    /// Which installation a call is about: the tenant, the connection id the secrets are stored
    /// under, and the settings already narrowed to the right type.
    ///
    /// <para>
    /// It exists because the ports take no connection — <c>StartCallbackAsync</c> receives a user,
    /// a number and a caller id and nothing else, deliberately, so that a consumer module never
    /// has to know a connection exists. <c>Settings</c> is non-null by construction: a row whose
    /// settings are not <c>RingoverSettings</c> never produces a binding, it produces a technical
    /// failure naming the problem.
    /// </para>
    /// </summary>
    private sealed record RingoverBinding(
        Guid TenantId, Guid ConnectionId, RingoverSettings Settings);
}
