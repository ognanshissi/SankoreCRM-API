namespace Sankore.Modules.Integration.Adapters.Orass;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Sankore.Modules.Integration.Domain;
using Sankore.Modules.Integration.Infrastructure;
using Sankore.Modules.Integration.Ports;
using Sankore.Modules.Integration.PublicApi;
using Sankore.Shared.Kernel;

/// <summary>
/// ORASS®Suite (ORSYS) — an insurer's back-office, reached through its external API or its
/// Bancassurance module where the insurer has opened one, and through bordereaux on the shared
/// batch socle where it has not (ASS-06).
///
/// ───────────────────────────────────────────────────────────────────────────────────────────────
/// <b>WHAT IS MISSING.</b> The ORASS interface specification, and the insurer's agreement. No
/// document in this repository defines an operation of the external API or of the Bancassurance
/// module, its request or response shape, its authentication, the bordereau layout, its encoding,
/// or the shape of the acknowledgement the insurer writes back. ASS-06's own last acceptance
/// criterion says so: « Prérequis : spécification d'interface ORASS et accord de l'assureur », and
/// <see cref="OrassSpecification.PlanReference"/> records it next to the three other adapters in
/// the same state.
///
/// <b>WHICH MAKES CRITERIA 1 AND 4 UNDELIVERABLE, AND 2 AND 3 DELIVERABLE IN FULL.</b> Criterion 1
/// (« l'adaptateur implémente les ports assurance sur l'API externe ou le module Bancassurance
/// d'ORASS, selon la spécification fournie ») and criterion 4 (« tests de contrat verts sur
/// l'environnement de test de l'assureur ») both need the insurer. Criterion 2 (« repli en mode
/// batch (bordereaux par fichier) sur le socle commun si l'API n'est pas ouverte ») and criterion 3
/// (the four <c>OrassSettings</c> parameters) need nothing from anybody: they are
/// <see cref="OrassCarrierRouting"/>, <see cref="OrassIntermediaryScope"/>,
/// <see cref="OrassCredential"/> and <see cref="OrassCapabilityMatrix"/>, and they are implemented
/// and tested here.
///
/// <b>WHAT ARRIVES WITH THE SPECIFICATION.</b> A file of field mappings and either a client or a
/// bordereau writer — not an architecture. The capability matrix, the carrier decision, the
/// coherence rule, the attribution guard, the credential resolution, the keyed registration, the
/// ports, the health answer and the test harness are all here and all behave. What every port
/// method does instead of building a request or a record is refuse. If ORSYS or the insurer hands
/// over an OpenAPI document, the client is GENERATED and not hand-written: M02's biometry client is
/// the precedent and the scar — hand-written wire records against a document nobody had read, not
/// one field name matching, every call mapping to null and every KYC file stuck in
/// <c>Verifying</c> against a service that answered perfectly.
///
/// <b>THE QUESTIONS TO SETTLE FIRST</b>, in the order they unblock work, are in
/// <see cref="OrassSpecification.OpenQuestions"/> — so that a procurement conversation and this
/// class quote the same list. The important structural point about them: <b>the API operation
/// catalogue and the bordereau layout plus its acknowledgement format are two separate asks</b>,
/// about two different carriers at two different insurers, and an answer to one unblocks nothing
/// about the other. That is why every refusal names only the artefact the installation in front of
/// the operator actually needs. And the asks go to two counterparties, not one: the specification
/// may come from ORSYS or from the insurer, while the AGREEMENT can only come from the insurer —
/// a message naming only the publisher would have somebody chase a vendor for a permission only
/// their partner can grant.
/// ───────────────────────────────────────────────────────────────────────────────────────────────
///
/// <para>
/// <b>Why a refusal and not a plausible mapping, and why the stake is higher here than in the
/// three core-banking adapters.</b> On the API carrier a guessed operation answers 404 and the
/// write is at least seen to fail. On the bordereau carrier it is silent and worse: a file in the
/// wrong layout is deposited successfully, the SFTP transfer reports success, and the command waits
/// out its <c>AckTimeoutHours</c> for an acknowledgement that will never come because the insurer
/// could not read what we sent. And the subject matter is what makes it the worst version of that
/// failure in this repository: a subscription that SANKORE records as sent and the insurer never
/// accepted is <b>a customer who believes they are insured and is not</b> — discovered at the
/// claim, by a family. An explicit <see cref="ErrorFamily.Technical"/> refusal is the honest state,
/// and <c>Technical</c> specifically: the dispatcher never retries it (retrying cannot obtain a
/// document or an agreement) and alerts the administrator instead.
/// </para>
///
/// <para>
/// <b>WHAT IT CLAIMS, AND WHAT IT DOES NOT.</b> <c>IInsurancePolicyPort</c> and
/// <c>IInsuranceClaimPort</c> are implemented. <c>IInsuranceProductPort</c> — the catalogue,
/// pricing and eligibility — is not implemented at all, and the two precedents disagree about what
/// that silence means, so the reasoning has to be explicit:
/// </para>
///
/// <list type="bullet">
/// <item><b>Why the two that are claimed.</b> ASS-06's story is « les souscriptions et sinistres
///   arrivent directement dans son back-office, afin d'éviter les bordereaux saisis à la main ».
///   Subscriptions and claims, with their lifecycle: subscribe, cancel, read back, fetch the
///   certificate; declare, read back, add the document the insurer asks for afterwards. Those are
///   exactly the two ports, and exactly the three operations the dispatcher has a
///   <c>CommandType</c> for.</item>
/// <item><b>Why the product port is not.</b> Pricing and eligibility belong to ASS-02 and ASS-03,
///   which are not in this lot and whose criteria say nothing about ORASS. Beyond scope, there are
///   two independent reasons, and they reach the same conclusion from the two different directions
///   the precedents each took. <c>PerfectVisionAdapter</c>'s direction: on the bordereau carrier a
///   price is not "not yet", it is NEVER — a one-way file deposit cannot quote anything, and
///   criterion 2 makes that carrier a first-class possibility for ORASS rather than an edge case.
///   <c>AmplitudeCapabilityMatrix</c>'s direction, for the API carrier: nothing tells us such an
///   operation exists, and that claim is not free — <c>IInsuranceProductPort</c>'s own remark says
///   a catalogue capability is « declared and not assumed » because a tenant that configures its
///   products by hand needs none. Declaring a price we cannot obtain would have whoever builds
///   ASS-03 quote a premium at a counter from an operation we have no evidence of. Two different
///   reasons for one absence, and the matrix cannot express the difference — which is why this
///   comment has to.</item>
/// <item><b>So the silence means different things per carrier</b>, and the day the specification
///   arrives the decision is reopened on the API side alone: if the catalogue has a pricing
///   operation, the port is added together with its mapper and its matrix entries in ONE change,
///   never the matrix first. On the batch side nothing reopens.</item>
/// </list>
///
/// <para>
/// <b>Registered as <c>ICbsAdapter</c> even though it serves the insurance family</b>, because
/// that is how this module resolves an insurance port: there is no <c>IInsuranceAdapter</c>:
/// <c>IntegrationAdapterResolver.ResolveAdapter</c> looks up a keyed <c>ICbsAdapter</c> by
/// <c>connection.Kind</c> for BOTH families, and <c>IntegrationModuleFacade</c>'s
/// <c>ResolveInsurancePort</c> goes through it before casting to the port it wants. The interface's
/// name is a legacy of the core-banking-first design and not a statement about the family; what it
/// actually carries — a kind, a capability matrix and a health check — is family-neutral.
/// </para>
///
/// <para>
/// <b>No call journal.</b> Every operating adapter wraps its calls in <c>ICallJournal</c>
/// (INT-08). This one makes no outbound call, and a journal row for a call that never touched a
/// back-office would read as a failure of the insurer rather than as a missing document on our
/// side. The refusal is returned to the dispatcher, which records it on the command where an
/// administrator is already looking.
/// </para>
/// </summary>
internal sealed class OrassAdapter(
    IntegrationDbContext db,
    ITenantContext tenant,
    ISecretsModule secrets,
    TimeProvider clock,
    ILogger<OrassAdapter> logger)
    : ICbsAdapter, IInsurancePolicyPort, IInsuranceClaimPort
{
    private IntegrationResult<OrassBinding>? _binding;

    public IntegrationKind Kind => IntegrationKind.Orass;

    /// <summary>
    /// The matrix of <see cref="OrassCapabilityMatrix"/>, applied to <b>the connection asked
    /// about</b> — the three writes in that row's carrier mode when it names an intermediary code,
    /// plus the three live reads where that insurer has opened an API, and nothing at all when its
    /// submissions cannot be attributed.
    ///
    /// <para>
    /// <b>This is the adapter the missing parameter hurt, and the mitigation is gone.</b> Until L8
    /// the contract asked a parameterless property, so this adapter loaded every ORASS row of the
    /// tenant and INTERSECTED their matrices (<c>OrassCapabilityMatrix.Narrowest</c>, now deleted),
    /// taking the weaker promise wherever two rows disagreed. That was the only safe answer to an
    /// unanswerable question and it was still wrong: an IMF distributing IARD and Vie holds two
    /// ACTIVE ORASS connections — separate undertakings in the CIMA zone — and both were told what
    /// ONLY BOTH could do, so a branch with an open API was reported batch because the other
    /// branch had none. Handed its connection, the honest per-row answer is simply available.
    /// </para>
    ///
    /// <para>
    /// Reads its two inputs off <paramref name="connection"/> and queries nothing. The multi-row
    /// <see cref="Bind"/> survives untouched for <see cref="RefusalAsync"/>, where the fail-closed
    /// sweep over every row is still right: a PORT method carries no connection, by design, so
    /// there the adapter genuinely cannot tell which row a call is for.
    /// </para>
    /// </summary>
    public IntegrationCapabilities CapabilitiesFor(IntegrationConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);

        // `as`, not a cast: an ORASS row carrying another kind's settings narrows to no matrix —
        // OrassCapabilityMatrix.For treats null as unattributable — rather than throwing out of a
        // screen that is only asking what is available.
        return OrassCapabilityMatrix.For(
            connection.Settings as OrassSettings, connection.Mode);
    }

    // ── Binding ─────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The tenant's ORASS connections, their ids, their settings and their modes — resolved once
    /// per scope.
    ///
    /// <para>
    /// <b>Read synchronously, and the reason is no longer the capability matrix.</b> It was: this
    /// paragraph used to argue that <c>ICbsAdapter.Capabilities</c> was a parameterless synchronous
    /// property on a contract the adapter could not change, while criterion 3 required the matrix
    /// to depend on the row — so the row had to be fetched from somewhere. That contract has since
    /// been corrected: <see cref="CapabilitiesFor"/> is handed its connection and reads nothing
    /// here.
    ///
    /// <para>
    /// What still needs this read is <c>RefusalAsync</c>, which has no connection to work from
    /// because the insurance PORT methods carry none — so a refusal has to be computed across all
    /// of the tenant's rows. The synchronous form remains the lesser evil for it: blocking on an
    /// async query with <c>GetAwaiter().GetResult()</c> is the deadlock-prone shape of the same
    /// thing, and reading in the constructor would query on every resolution of the adapter,
    /// including the resolutions that never refuse anything. One bounded read, taken lazily, cached
    /// for the scope.
    /// </para>
    ///
    /// <para>
    /// <b>ALL of the tenant's ORASS rows, not one</b> — the only binding in this module that is a
    /// list, and it is the insurance family that makes it one. A tenant has at most one active core
    /// banking connection (<c>ux_integration_connection_active_core_banking</c>) but may have
    /// several active insurance ones (ASS-01), and the IARD/Vie separation makes two ORASS rows for
    /// one insurer group the normal shape for an IMF selling both credit-life and non-life cover.
    /// Picking one and calling it "the tenant's ORASS connection" would answer a screen about the
    /// other branch. The list is ordered deterministically all the same, so the primary row is
    /// stable across two reads.
    /// </para>
    ///
    /// <para>
    /// <b>Activation is NOT part of the predicate</b>, unlike <c>TemenosAdapter.BindAsync</c>. An
    /// ORASS connection can never be activated — its health check cannot pass until the
    /// specification arrives — so filtering on <c>IsActive</c> would make the matrix permanently
    /// empty and the deliverable half of this chantier unobservable. Reading an inactive row grants
    /// nothing: every port method refuses regardless.
    /// </para>
    ///
    /// <para>
    /// <c>IgnoreQueryFilters</c> paired with an explicit tenant predicate, as every background path
    /// in this module does: the adapter is reached from the Hangfire dispatcher, where the ambient
    /// tenant comes from <c>BackgroundJobContext.SetScope</c>, and neither guard is load-bearing
    /// alone.
    /// </para>
    ///
    /// <para>
    /// The three failures are told apart because they send an administrator to three different
    /// screens: no tenant on the scope is a composition fault, no ORASS connection is INT-03's
    /// configuration screen, and settings of the wrong shape is a row that must be corrected. The
    /// last one fails the WHOLE binding even when other rows are sound — fail-closed, because the
    /// call path cannot tell which row a call is about and a corrupt row may be the one it is for.
    /// </para>
    /// </summary>
    private IntegrationResult<OrassBinding> Bind()
    {
        if (_binding is not null) return _binding;

        if (!tenant.HasTenant || tenant.CurrentTenantId == Guid.Empty)
        {
            return _binding = IntegrationResult.Technical<OrassBinding>(
                IntegrationErrors.NoActiveConnection,
                "No tenant is established on this scope, so no ORASS connection can be resolved.");
        }

        var tenantId = tenant.CurrentTenantId;

        var connections = db.Connections
            .IgnoreQueryFilters()
            .Where(c => c.TenantId == tenantId && c.Kind == IntegrationKind.Orass)
            .OrderByDescending(c => c.IsActive)
            .ThenBy(c => c.CreatedAt)
            .ThenBy(c => c.Id)
            .ToList();

        if (connections.Count == 0)
        {
            return _binding = IntegrationResult.Technical<OrassBinding>(
                IntegrationErrors.NoActiveConnection,
                "This tenant has no ORASS insurance connection.");
        }

        var installations = new List<OrassInstallation>(connections.Count);

        foreach (var connection in connections)
        {
            if (connection.Settings is not OrassSettings settings)
            {
                // An ORASS row carrying another kind's settings is a corrupted configuration, not a
                // missing specification: the two send an administrator to different screens, so
                // they are told apart even though both end in no matrix.
                logger.LogError(
                    "Connection {ConnectionId} is an ORASS row whose settings are not "
                    + "OrassSettings; this tenant's capability matrix will be empty and every call "
                    + "will refuse.",
                    connection.Id);

                return _binding = IntegrationResult.Technical<OrassBinding>(
                    IntegrationErrors.SettingsInvalid,
                    "An ORASS connection of this tenant does not carry ORASS settings.");
            }

            installations.Add(new OrassInstallation(connection.Id, settings, connection.Mode));
        }

        if (installations.Count > 1)
        {
            // Not an error — it is the shape an IMF selling both branches has. It no longer
            // narrows the capability matrix (CapabilitiesFor answers per connection), but it does
            // still make RefusalAsync fail-closed across every row, so a refusal may name a field
            // on a connection other than the one the caller had in mind. Worth a line for whoever
            // reads that refusal.
            logger.LogInformation(
                "Tenant {TenantId} has {Count} ORASS connections; a port refusal is computed over "
                + "all of them, because a port call carries no connection.",
                tenantId, installations.Count);
        }

        return _binding = IntegrationResult.Ok(new OrassBinding(tenantId, installations));
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
    /// <b>IT ALSO CANNOT BE MADE TO PASS, AND FOR ORASS THAT IS THE ONLY THING BETWEEN A CONFIGURED
    /// CONNECTION AND AN INVENTED BORDEREAU.</b> <c>IntegrationConnection.Activate</c> refuses
    /// without a successful check, so an ORASS connection stays inactive, and an inactive
    /// connection can never be reached: the insurance gateway enqueues against an ACTIVE insurance
    /// connection and lists policies and claims from active ones only. The stake is sharper here
    /// than for Perfect Vision or SAB, for a reason specific to this kind:
    /// <c>ExecuteIntegrationCommandHandler</c> routes a write to the batch socle on the connection's
    /// MODE, <b>before</b> resolving an adapter, and <see cref="OrassSettings"/> is file-based — so
    /// on an active <c>Batch</c> connection the subscriptions would never reach this class at all
    /// and none of its refusals would ever be consulted. <c>OutboundBatchFileGenerator</c> would
    /// fall back to <c>DelimitedOutboundBatchFormatter</c> — an honest self-describing projection of
    /// what the platform owes, and emphatically not an ORASS bordereau — the SFTP deposit would
    /// report success, and every subscription would wait out its <c>AckTimeoutHours</c> while a
    /// counter clerk had already told the customer their cover was in force. The health gate is the
    /// only thing in that path, which is why this method must never return healthy and why a test
    /// pins exactly that.
    /// </para>
    ///
    /// <para>
    /// One asymmetry with SAB worth recording, because it cuts the other way: an unactivatable ORASS
    /// row displaces nothing. SAB's would occupy the tenant's single active core-banking slot; the
    /// insurance family has no such index — several active connections are the point (ASS-01) — so
    /// an ORASS row waiting for its specification sits harmlessly beside an insurer that works.
    /// </para>
    ///
    /// <para>
    /// <b>It knows WHICH connection it is about</b> — it takes one explicitly, because it runs
    /// before activation on the row the administrator is editing, so the branch, the carrier and
    /// the credential are all read from the row in front of the operator. Since L8 it shares that
    /// with <see cref="CapabilitiesFor"/>; what remains unique to the gate is that it is the only
    /// place the question "is THIS insurer connection fit to be USED" is asked, which is a
    /// stronger question than what the row can do. The port methods still know no connection, and
    /// <see cref="RefusalAsync"/> is fail-closed across every row for that reason.
    /// </para>
    ///
    /// <para>
    /// <b>Four configuration faults are reported first, and separately</b>, in the order of a real
    /// submission: settings of the wrong shape, no intermediary code (who is submitting), a mode
    /// that contradicts the coordinates (which carrier), and the carrier's missing credential (what
    /// authenticates it). All four are mistakes an administrator can correct themselves, on their
    /// own screens; only the fifth is a conversation with ORSYS and the insurer. Collapsing any of
    /// them into the others would send somebody to a partner over a field they could have fixed, or
    /// — worse — have them wait for a document that would not fix anything.
    /// </para>
    ///
    /// <para>
    /// No latency is reported, since nothing was called. Reporting zero would put an ORASS row in
    /// the activation screen's latency column next to systems that actually answered.
    /// </para>
    /// </summary>
    public async Task<IntegrationHealth> CheckHealthAsync(
        IntegrationConnection connection, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(connection);

        var checkedAt = clock.GetUtcNow();

        if (connection.Settings is not OrassSettings settings)
        {
            return IntegrationHealth.Unhealthy(
                $"{IntegrationErrors.SettingsInvalid}: this ORASS connection does not carry ORASS "
                + "settings.",
                checkedAt);
        }

        // Attribution first, always — see OrassIntermediaryScope for why the direction is not a
        // matter of taste when a submission that cannot be attributed is a policy in nobody's
        // portfolio.
        if (OrassIntermediaryScope.ResolveFor(settings) != OrassIntermediaryResolution.Resolved)
        {
            return IntegrationHealth.Unhealthy(
                $"{IntegrationErrors.SettingsInvalid}: {OrassIntermediaryScope.MissingDetail()}",
                checkedAt);
        }

        // Before the credential, because the carrier is what decides WHICH credential is wanted:
        // an incoherent row has no carrier, so a credential verdict on it would name an arbitrary
        // one of the two.
        var incoherence = OrassCarrierRouting.IncoherenceDetail(settings, connection.Mode);

        if (incoherence is not null)
        {
            return IntegrationHealth.Unhealthy(
                $"{IntegrationErrors.SettingsInvalid}: {incoherence}", checkedAt);
        }

        var carrier = OrassCarrierRouting.ChooseFor(settings, connection.Mode);

        // Read from the connection we were handed and not from the binding: the health check runs
        // BEFORE a connection is active, and on a row the caller may have just created, so there is
        // nothing for Bind() to find.
        var credential = await OrassCredential.ProbeAsync(
            secrets, connection.TenantId, connection.Id, carrier, ct);

        if (credential.State == OrassCredentialState.Missing)
        {
            return IntegrationHealth.Unhealthy(
                $"{IntegrationErrors.CredentialMissing}: {credential.MissingDetail}", checkedAt);
        }

        return IntegrationHealth.Unhealthy(
            $"{IntegrationErrors.AdapterSpecificationPending}: "
            + OrassSpecification.HealthDetail(settings.Branch, carrier),
            checkedAt);
    }

    // ── IInsurancePolicyPort (ASS-04, ASS-07) ───────────────────────────────────────────────

    /// <summary>
    /// Refuses, naming whichever of the four things is missing.
    ///
    /// <para>
    /// A subscription is the first thing either carrier would move and the one whose shape is most
    /// completely unknown: <c>InsurancePolicyPayload</c> carries a product code that has to be
    /// translated through <c>integration_mapping</c> into the insurer's own vocabulary, a
    /// periodicity, an effective date, and a list of beneficiaries with their names, their
    /// relationship and their shares — and a mapping table cannot be filled in without the target
    /// vocabulary either, on either carrier.
    /// </para>
    ///
    /// <para>
    /// It is also the method where guessing would cost the most. Every other blocked adapter's
    /// worst case is a write the back-office never accepted; here that write is a policy, so the
    /// worst case is a customer who was told at a counter that they are covered. That is the whole
    /// reason this adapter exists in this shape.
    /// </para>
    /// </summary>
    public Task<IntegrationResult<ExternalId>> SubscribeAsync(
        InsurancePolicyPayload payload, IdempotencyKey key, CancellationToken ct)
        => RefuseAsync<ExternalId>(OrassOperations.SubscribePolicy, ct);

    /// <summary>
    /// Refuses. Declared <see cref="CapabilityMode.RealTime"/> in the matrix only where the insurer
    /// has opened an API; where it has not, the capability is absent and
    /// <c>IntegrationAdapterResolver.ResolvePort</c> stops the call before it reaches here — which
    /// is the honest outcome, since nothing in this socle defers a read to a file cycle and this
    /// module owns no policy table to read from instead.
    /// </summary>
    public Task<IntegrationResult<IReadOnlyList<InsurancePolicy>>> GetPoliciesAsync(
        ExternalId customerId, CancellationToken ct)
        => RefuseAsync<IReadOnlyList<InsurancePolicy>>(OrassOperations.ReadPolicies, ct);

    /// <inheritdoc cref="GetPoliciesAsync"/>
    public Task<IntegrationResult<InsurancePolicy>> GetPolicyAsync(
        ExternalId policyId, CancellationToken ct)
        => RefuseAsync<InsurancePolicy>(OrassOperations.ReadPolicies, ct);

    /// <summary>
    /// Refuses. A certificate is evidence handed to a customer, which is why guessing here would be
    /// the worst place to guess: <c>InsuranceCertificate</c> carries bytes, a content type and a
    /// file name, and a document fetched from a guessed operation is a document whose provenance
    /// nobody can establish while it is being printed for somebody's loan file.
    /// </summary>
    public Task<IntegrationResult<InsuranceCertificate>> GetCertificateAsync(
        ExternalId policyId, CancellationToken ct)
        => RefuseAsync<InsuranceCertificate>(OrassOperations.IssueCertificate, ct);

    /// <summary>
    /// Refuses. A cancellation is the one write whose silent failure is as bad as a subscription's
    /// and in the opposite direction: the customer believes the cover has stopped and the premium
    /// keeps being due — or, worse, they believe it has stopped and it has not, which on a
    /// credit-life policy sold with a loan means a debt still insured against a person who thinks
    /// they ended it.
    /// </summary>
    public Task<IntegrationResult> CancelAsync(
        ExternalId policyId, string reason, IdempotencyKey key, CancellationToken ct)
        => RefuseAsync(OrassOperations.CancelPolicy, ct);

    // ── IInsuranceClaimPort (ASS-09) ────────────────────────────────────────────────────────

    /// <inheritdoc cref="SubscribeAsync"/>
    public Task<IntegrationResult<ExternalId>> DeclareAsync(
        InsuranceClaimPayload payload, IdempotencyKey key, CancellationToken ct)
        => RefuseAsync<ExternalId>(OrassOperations.DeclareClaim, ct);

    /// <inheritdoc cref="GetPoliciesAsync"/>
    public Task<IntegrationResult<IReadOnlyList<InsuranceClaim>>> GetClaimsAsync(
        ExternalId policyId, CancellationToken ct)
        => RefuseAsync<IReadOnlyList<InsuranceClaim>>(OrassOperations.ReadClaims, ct);

    /// <inheritdoc cref="GetPoliciesAsync"/>
    public Task<IntegrationResult<InsuranceClaim>> GetClaimAsync(
        ExternalId claimId, CancellationToken ct)
        => RefuseAsync<InsuranceClaim>(OrassOperations.ReadClaims, ct);

    /// <summary>
    /// Refuses, under <see cref="OrassOperations.AddClaimDocument"/> — the one operation name with
    /// no <see cref="IntegrationCapability"/> of its own.
    ///
    /// <para>
    /// It rides on <see cref="IntegrationCapability.DeclareClaim"/>: the supporting document an
    /// insurer asks for after a declaration belongs to the declaration, there is no
    /// <c>CommandType</c> that could queue it, and inventing a capability for it would declare an
    /// operation the dispatcher has no route for. Worth knowing before somebody adds one — the
    /// method takes an <c>IdempotencyKey</c> and is therefore a write, so a capability would need a
    /// command type and a dispatcher route in the same change.
    /// </para>
    /// </summary>
    public Task<IntegrationResult> AddDocumentAsync(
        ExternalId claimId, string storageRef, IdempotencyKey key, CancellationToken ct)
        => RefuseAsync(OrassOperations.AddClaimDocument, ct);

    // ── The refusal chain, in one place ─────────────────────────────────────────────────────

    /// <summary>
    /// Attribute, carry, authenticate, build — and stop at the first one that cannot be satisfied.
    ///
    /// <para>
    /// One method for the whole adapter, so that no port method can be written that skips a step.
    /// That is the entire guarantee of criterion 3 at call time: the intermediary check is not a
    /// line somebody remembered to put in <see cref="SubscribeAsync"/>, it is the only path to a
    /// result. The order is the order of a real submission — say who is submitting, work out what
    /// it travels on, authenticate it, then build it — and each step names a different owner (the
    /// settings screen, the settings screen again, the secret endpoint, the insurer), so an
    /// operator is sent to the one place that can move their problem forward.
    /// </para>
    ///
    /// <para>
    /// <b>Every guard runs over ALL of the tenant's ORASS connections, and refuses if any one of
    /// them fails.</b> Fail-closed, because the port methods carry no connection — a subscription
    /// arrives as a payload and an idempotency key, deliberately, so that a consumer module never
    /// has to know a connection exists — and the adapter therefore cannot tell which of a tenant's
    /// ORASS rows a call is for. Refusing a call that might have worked costs an administrator one
    /// field and a retry; letting one through on the assumption that the sound row is the one being
    /// used costs a policy in nobody's portfolio.
    /// </para>
    /// </summary>
    private async Task<OrassRefusal> RefusalAsync(string operation, CancellationToken ct)
    {
        var bound = Bind();

        // No connection, no tenant, or settings of the wrong shape. Forwarded as-is: each already
        // names the screen that owns it.
        if (bound.IsFailure) return new OrassRefusal(bound.Code!, bound.Detail!);

        var binding = bound.Value;

        // 1. ATTRIBUTE. First, always, and fail-closed.
        foreach (var installation in binding.Installations)
        {
            if (!OrassIntermediaryScope.IsAttributed(installation.Settings))
            {
                return new OrassRefusal(
                    IntegrationErrors.SettingsInvalid, OrassIntermediaryScope.MissingDetail());
            }
        }

        // 2. CARRY. A row whose coordinates and mode contradict each other has no carrier at all,
        //    so it is refused before anything is said about credentials.
        foreach (var installation in binding.Installations)
        {
            var incoherence = OrassCarrierRouting.IncoherenceDetail(
                installation.Settings, installation.Mode);

            if (incoherence is not null)
                return new OrassRefusal(IntegrationErrors.SettingsInvalid, incoherence);
        }

        // 3. AUTHENTICATE. Presence only, and from the vault — never the value, which this process
        //    has no use for until the specification says how it travels. Probed for the carrier
        //    each row would actually use, which for ORASS is two different sets of entries.
        foreach (var installation in binding.Installations)
        {
            var probe = await OrassCredential.ProbeAsync(
                secrets,
                binding.TenantId,
                installation.ConnectionId,
                OrassCarrierRouting.ChooseFor(installation.Settings, installation.Mode),
                ct);

            if (probe.State == OrassCredentialState.Missing)
                return new OrassRefusal(IntegrationErrors.CredentialMissing, probe.MissingDetail!);
        }

        // 4. BUILD. Everything our side owns is in place; what is missing is the specification and
        //    the agreement.
        return new OrassRefusal(
            IntegrationErrors.AdapterSpecificationPending,
            OrassSpecification.RefusalDetail(
                operation, SharedBranch(binding), SharedCarrier(binding)));
    }

    /// <summary>
    /// <see cref="ErrorFamily.Technical"/> for every branch, and never
    /// <see cref="ErrorFamily.Transient"/>. The family decides what the platform does next:
    /// <c>Technical</c> parks the command and alerts an administrator, which is right for all four
    /// causes, while <c>Transient</c> would have the dispatcher retry a missing intermediary code, a
    /// missing credential and a missing document with exponential backoff forever.
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

    /// <summary>
    /// The branch every ORASS connection of this tenant declares, or <c>null</c> when they do not
    /// agree.
    ///
    /// <para>
    /// Null rather than the first one, for the reason <c>AmplitudeSpecification.MissingDocumentFor</c>
    /// gives about its release: naming the wrong one would send a conversation to the wrong company.
    /// For ORASS the two companies are legally separate undertakings in the CIMA zone, so "the life
    /// branch" and "the non-life branch" are not two labels on one counterparty — and
    /// <see cref="OrassIntermediaryScope.Designation"/> names both when it is handed null.
    /// </para>
    /// </summary>
    private static OrassBranch? SharedBranch(OrassBinding binding)
    {
        var branches = binding.Installations.Select(i => i.Settings.Branch).Distinct().ToList();

        return branches.Count == 1 ? branches[0] : null;
    }

    /// <summary>
    /// The carrier every ORASS connection of this tenant resolves to, or <c>null</c> when they do
    /// not agree — in which case the refusal names both missing artefacts rather than guessing
    /// which ask is on the critical path.
    /// </summary>
    private static OrassCarrier? SharedCarrier(OrassBinding binding)
    {
        var carriers = binding.Installations
            .Select(i => OrassCarrierRouting.ChooseFor(i.Settings, i.Mode))
            .Distinct()
            .ToList();

        return carriers.Count == 1 ? carriers[0] : null;
    }

    /// <summary>A code and a detail, so one chain can serve both result shapes.</summary>
    private readonly record struct OrassRefusal(string Code, string Detail);

    /// <summary>
    /// One ORASS connection of the tenant: the id its vault entries are keyed by, the settings
    /// already narrowed to the right type, and the mode the dispatcher routes its writes on.
    ///
    /// <para>
    /// <c>Settings</c> is non-null by construction: a row whose settings are not
    /// <see cref="OrassSettings"/> never produces an installation, it fails the whole binding with
    /// a technical result naming the problem.
    /// </para>
    /// </summary>
    private sealed record OrassInstallation(
        Guid ConnectionId, OrassSettings Settings, IntegrationMode Mode);

    /// <summary>
    /// Which installations a call could be about: the tenant, and every ORASS connection it has.
    ///
    /// <para>
    /// A list and not a single row, unlike <c>SabAdapter</c>'s binding, for the reason
    /// <see cref="Bind"/> sets out — insurance is the family where a tenant may legitimately have
    /// several connections of one kind, and the ports take none as an argument.
    /// </para>
    /// </summary>
    private sealed record OrassBinding(Guid TenantId, IReadOnlyList<OrassInstallation> Installations);
}
