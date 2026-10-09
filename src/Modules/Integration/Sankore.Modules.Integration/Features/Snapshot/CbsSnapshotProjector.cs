namespace Sankore.Modules.Integration.Features.Snapshot;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Sankore.Modules.Integration.Domain;
using Sankore.Modules.Integration.Features.References.GetReference;
using Sankore.Modules.Integration.Infrastructure;
using Sankore.Modules.Integration.Infrastructure.CallLog;
using Sankore.Modules.Integration.Ports;
using Sankore.Modules.Integration.PublicApi;
using Sankore.Modules.Kyc.PublicApi;
using Sankore.Shared.Infrastructure.Messaging;

/// <summary>
/// The writer of <c>cbs_customer_snapshot</c> (INT-21, criteria 1, 2 and 4).
///
/// <para>
/// Reads the customer's accounts, loans and flow from the CBS, translates the CBS product codes
/// into CRM codes, adds up the two totals, upserts the one row the composite key allows, and
/// reports — never corrects — a disagreement with M02 about the KYC tier.
/// </para>
///
/// <para>
/// <b>An all-or-nothing projection.</b> A read that fails abandons the whole refresh and leaves
/// the previous row untouched. The alternative — storing what did answer and zeroing the rest —
/// is the trap this read model is most exposed to: a zero <c>monthly_flow</c> is indistinguishable
/// on screen from a customer who moved no money, and it is compared against a compliance ceiling.
/// Yesterday's figures with yesterday's <c>SnapshotAt</c> are honest; a fresh timestamp over a
/// half-read snapshot is not.
/// </para>
///
/// <para>
/// <b>It commits.</b> The snapshot row and the mismatch event go in one
/// <c>SaveChangesAsync</c> — the outbox publisher writes into this same DbContext without saving,
/// so the event cannot reach the broker describing a snapshot that was rolled back, nor be lost
/// while the snapshot stands. There is no outer unit of work per customer to enlist in: the caller
/// is a synchronisation iterating over customers, and one failed customer must not discard the
/// ones already projected.
/// </para>
/// </summary>
internal sealed class CbsSnapshotProjector(
    IntegrationDbContext db,
    IntegrationAdapterResolver resolver,
    ReferenceLookup references,
    SnapshotCodeTranslator translator,
    CbsKycLevelReader cbsKycLevels,
    ICallJournal journal,
    IKycModule kyc,
    [FromKeyedServices(nameof(IntegrationDbContext))] IEventPublisher publisher,
    TimeProvider clock,
    ILogger<CbsSnapshotProjector> logger) : ICbsSnapshotProjector
{
    /// <summary>
    /// Journal operation names. Constants because <c>operation</c> is the grouping key of the
    /// call-log stats endpoint: a name that varies per call turns that screen into one row per
    /// call.
    /// </summary>
    private const string GetAccountsOperation = "GetAccounts";
    private const string GetLoansOperation = "GetLoans";
    private const string GetMonthlyFlowOperation = "GetMonthlyFlow";

    public async Task ProjectAsync(
        Guid tenantId, Guid connectionId, Guid crmCustomerId, CancellationToken ct)
    {
        if (tenantId == Guid.Empty || connectionId == Guid.Empty || crmCustomerId == Guid.Empty)
            return;

        var connection = await resolver.ResolveConnectionAsync(tenantId, connectionId, ct);
        if (connection.IsFailure)
        {
            logger.LogWarning(
                "Snapshot skipped: connection {ConnectionId} of tenant {TenantId} — {Code}",
                connectionId, tenantId, connection.Code);
            return;
        }

        var adapter = resolver.ResolveFor(connection.Value);
        if (adapter.IsFailure)
        {
            logger.LogWarning(
                "Snapshot skipped: no adapter for connection {ConnectionId} — {Code}",
                connectionId, adapter.Code);
            return;
        }

        // ── Criterion 3, on the write side ──────────────────────────────────
        // No reference means this connection has never been given an identifier for this customer,
        // which is the ordinary state of a customer the CBS has never heard of. NOTHING is
        // written: an empty snapshot row would make the facade answer a snapshot of zeros instead
        // of null, and Customer 360 would display those zeros as the customer's balances. The
        // absence of the row IS the "does not exist in the CBS" answer.
        var externalId = await references.GetExternalIdAsync(
            tenantId, connectionId, IntegrationEntityTypes.Customer, crmCustomerId, ct);

        if (string.IsNullOrWhiteSpace(externalId))
        {
            logger.LogDebug(
                "Snapshot skipped: customer {CrmCustomerId} is unknown to connection {ConnectionId}",
                crmCustomerId, connectionId);
            return;
        }

        var customer = new ExternalId(externalId);

        // ── Criterion 1: what the CBS holds ─────────────────────────────────

        var accounts = await ReadAccountsAsync(tenantId, connectionId, adapter.Value, customer, ct);
        if (accounts is null) return;

        var loans = await ReadLoansAsync(tenantId, connectionId, adapter.Value, customer, ct);
        if (loans is null) return;

        // M02's answer serves two purposes and is read once: it carries the flow WINDOW the
        // monthly figure must be measured over, and the CRM tier criterion 4 compares.
        var limits = await kyc.GetLimitsAsync(tenantId, crmCustomerId, ct);

        var flow = await ReadMonthlyFlowAsync(
            tenantId, connectionId, adapter.Value, customer, limits?.WindowDays, ct);
        if (flow is null) return;

        // ── Criterion 2: CBS codes become CRM codes BEFORE storage ──────────
        var crmAccounts = await translator.TranslateAccountsAsync(tenantId, connectionId, accounts, ct);
        var crmLoans = await translator.TranslateLoansAsync(tenantId, connectionId, loans, ct);

        // The port first, the inference second (INT-21 criterion 1). ICbsKycLevelPort is optional
        // precisely because adapters differ in kind on it: Transact can be asked for a party's KYC
        // status, a batch-file CBS accepts the write and exposes no query. When the adapter does
        // answer, the figure is what the CBS actually holds — which is the only way criterion 4's
        // divergence can see a tier an officer changed INSIDE the CBS. When it does not, we fall
        // back to the last tier the CBS acknowledged from us, and that fallback is blind to
        // exactly that case; CbsKycLevelReader documents the limit.
        var cbsLevel = await ReadCbsKycLevelAsync(
            tenantId, connectionId, crmCustomerId, adapter.Value, customer, ct);

        // ── The upsert ──────────────────────────────────────────────────────
        // AsTracking because the context defaults to NoTracking (module convention) and this one
        // is here to be mutated. IgnoreQueryFilters plus the explicit tenant predicate because the
        // caller is a background job with no ambient tenant.
        var snapshot = await db.CbsSnapshots
            .IgnoreQueryFilters()
            .AsTracking()
            .FirstOrDefaultAsync(s => s.TenantId == tenantId && s.CrmCustomerId == crmCustomerId, ct);

        if (snapshot is null)
        {
            snapshot = CbsSnapshot.Create(tenantId, crmCustomerId, connectionId, clock);
            db.CbsSnapshots.Add(snapshot);
        }

        snapshot.Update(
            accountsJson: SnapshotSerialization.Serialise(crmAccounts),
            loansJson: SnapshotSerialization.Serialise(crmLoans),
            // Added up here and in no other place: the totals must agree with the two lists stored
            // next to them, and a reader that summed them itself would be a second definition.
            //
            // Account balances are summed rather than fetched one by one through GetBalanceAsync:
            // CbsAccount already carries the balance the account list answered with, and N extra
            // calls per customer per synchronisation would multiply the load on the CBS by the
            // size of each portfolio for a figure we already hold. A multi-currency portfolio adds
            // up to a figure in no single currency — the column is one decimal, so that limitation
            // is the entity's, not this method's.
            totalBalance: crmAccounts.Sum(a => a.Balance),
            monthlyFlow: flow.Value,
            kycLevelInCbs: cbsLevel,
            clock: clock);

        // ── Criterion 4: report, never correct ──────────────────────────────
        var crmLevel = SnapshotKycLevels.FromLimits(limits);

        if (SnapshotKycLevels.Diverge(crmLevel, cbsLevel))
        {
            // Through the outbox, so it is committed by the SaveChangesAsync below together with
            // the snapshot it describes. Which side is right is a compliance decision, so nothing
            // here pushes a tier either way — INT-21 reports, and M02 or an officer decides.
            await publisher.PublishAsync(
                new CbsKycMismatchDetectedEvent(
                    TenantId: tenantId,
                    CrmCustomerId: crmCustomerId,
                    CrmLevel: crmLevel.ToString(),
                    CbsLevel: cbsLevel!.Value.ToString(),
                    DetectedAt: clock.GetUtcNow()),
                ct);

            logger.LogInformation(
                "KYC tier divergence on customer {CrmCustomerId}: CRM {CrmLevel}, CBS {CbsLevel}",
                crmCustomerId, crmLevel, cbsLevel);
        }

        await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// The accounts, or <c>null</c> to abandon the projection.
    ///
    /// <para>
    /// The load-bearing read: an installation that cannot answer it has nothing this read model
    /// could usefully hold, and a snapshot written without it would publish an empty portfolio for
    /// a customer who has one. So an unsupported capability abandons as squarely as a failed call.
    /// </para>
    /// </summary>
    /// <summary>
    /// The tier the CBS holds: asked of the adapter when it declares
    /// <see cref="IntegrationCapability.ReadKycLevel"/>, inferred from our acknowledged writes
    /// otherwise.
    ///
    /// <para>
    /// A port failure does NOT fall back to the inference. The two answer different questions —
    /// "what the CBS holds" and "what the CBS last accepted from us" — and silently substituting
    /// one for the other would report a divergence resolved when the read simply failed. An
    /// unavailable port yields null, which the snapshot stores as "no opinion" and the divergence
    /// check reads as "not a mismatch".
    /// </para>
    /// </summary>
    private async Task<KycLevel?> ReadCbsKycLevelAsync(
        Guid tenantId,
        Guid connectionId,
        Guid crmCustomerId,
        ResolvedAdapter adapter,
        ExternalId customer,
        CancellationToken ct)
    {
        if (adapter.Adapter is ICbsKycLevelPort port
            && adapter.Capabilities.Supports(IntegrationCapability.ReadKycLevel))
        {
            var read = await journal.RecordAsync(
                new CallContext(tenantId, connectionId, "read-kyc-level"),
                inner => port.GetKycLevelAsync(customer, inner),
                ct);

            if (read.IsSuccess) return read.Value;

            logger.LogWarning(
                "Snapshot: the KYC tier could not be read from the CBS for customer "
                + "{CrmCustomerId} ({Family}/{Code}); the snapshot records no CBS tier rather "
                + "than falling back to our own last write.",
                crmCustomerId, read.Family, read.Code);

            return null;
        }

        return await cbsKycLevels.ReadAsync(tenantId, connectionId, crmCustomerId, ct);
    }

    private async Task<IReadOnlyList<CbsAccount>?> ReadAccountsAsync(
        Guid tenantId, Guid connectionId, ResolvedAdapter adapter, ExternalId customer, CancellationToken ct)
    {
        var port = resolver.ResolvePort<ICbsAccountPort>(adapter, IntegrationCapability.ReadAccounts);
        if (port.IsFailure)
        {
            logger.LogWarning(
                "Snapshot skipped: {Kind} cannot read accounts — {Code}", adapter.Adapter.Kind, port.Code);
            return null;
        }

        var result = await journal.RecordAsync(
            Context(tenantId, connectionId, GetAccountsOperation),
            c => port.Value.GetAccountsAsync(customer, c),
            ct);

        if (result.IsSuccess) return result.Value;

        // Includes the second shape of "absent from the CBS": we hold a reference, and the CBS
        // answers ExternalEntityNotFound for it. Still no row written, and still the previous one
        // left standing — the journal row above is where that answer is recorded.
        logger.LogWarning(
            "Snapshot abandoned: accounts of connection {ConnectionId} — {Family}/{Code}",
            connectionId, result.Family, result.Code);

        return null;
    }

    /// <summary>
    /// The loans, or <c>null</c> to abandon.
    ///
    /// <para>
    /// An adapter that does not declare <see cref="IntegrationCapability.ReadLoans"/> contributes
    /// an EMPTY list rather than abandoning: a CBS with no credit module has no loans to report,
    /// and that is a fact about the installation. A declared capability whose call then FAILS does
    /// abandon — the difference between "there are none" and "we could not find out" is the whole
    /// reason the two branches are not the same.
    /// </para>
    /// </summary>
    private async Task<IReadOnlyList<CbsLoan>?> ReadLoansAsync(
        Guid tenantId, Guid connectionId, ResolvedAdapter adapter, ExternalId customer, CancellationToken ct)
    {
        var port = resolver.ResolvePort<ICbsLoanPort>(adapter, IntegrationCapability.ReadLoans);
        if (port.IsFailure) return [];

        var result = await journal.RecordAsync(
            Context(tenantId, connectionId, GetLoansOperation),
            c => port.Value.GetLoansAsync(customer, c),
            ct);

        if (result.IsSuccess) return result.Value;

        logger.LogWarning(
            "Snapshot abandoned: loans of connection {ConnectionId} — {Family}/{Code}",
            connectionId, result.Family, result.Code);

        return null;
    }

    /// <summary>
    /// Everything that moved over the window, or <c>null</c> to abandon. Same asymmetry as the
    /// loans: an undeclared capability is a zero the installation genuinely has, a failed call is
    /// not.
    /// </summary>
    private async Task<decimal?> ReadMonthlyFlowAsync(
        Guid tenantId,
        Guid connectionId,
        ResolvedAdapter adapter,
        ExternalId customer,
        int? windowDays,
        CancellationToken ct)
    {
        var port = resolver.ResolvePort<ICbsTransactionPort>(
            adapter, IntegrationCapability.ReadMonthlyFlow);

        if (port.IsFailure) return 0m;

        var total = 0m;

        foreach (var month in SnapshotFlowWindow.MonthsEndingAt(clock.GetUtcNow(), windowDays))
        {
            var result = await journal.RecordAsync(
                Context(tenantId, connectionId, GetMonthlyFlowOperation),
                c => port.Value.GetMonthlyFlowAsync(customer, month, c),
                ct);

            if (result.IsFailure)
            {
                // One missing month makes the whole window wrong, and the figure is compared
                // against a ceiling — so the partial sum is discarded rather than stored low.
                logger.LogWarning(
                    "Snapshot abandoned: monthly flow {Month} of connection {ConnectionId} — {Family}/{Code}",
                    month, connectionId, result.Family, result.Code);

                return null;
            }

            total += result.Value.Total;
        }

        return total;
    }

    /// <summary>
    /// The journal context for one read.
    ///
    /// <para>
    /// No endpoint and no command id: a synchronisation read is not serving a queued write, and
    /// the URL belongs to whichever adapter actually calls one. What matters is that the read is
    /// journalled at all — "was the CBS answering at 02:15" must be answerable about the nightly
    /// synchronisation exactly as it is about a counter operation. The customer reference is
    /// deliberately absent: see <c>CallContext</c>.
    /// </para>
    /// </summary>
    private static CallContext Context(Guid tenantId, Guid connectionId, string operation)
        => new(tenantId, connectionId, operation);
}
