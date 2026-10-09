namespace Sankore.Modules.Integration.Features.Reconciliation;

using Hangfire;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Sankore.Modules.Customers.PublicApi;
using Sankore.Modules.Integration.Domain;
using Sankore.Modules.Integration.Features.Snapshot;
using Sankore.Modules.Integration.Infrastructure;
using Sankore.Modules.Integration.PublicApi;
using Sankore.Modules.Kyc.PublicApi;
using Sankore.Shared.Infrastructure.BackgroundJobs;
using Sankore.Shared.Infrastructure.Messaging;

/// <summary>
/// Hangfire job — <b>the daily CRM ↔ external-system comparison of ONE tenant (INT-34)</b>.
/// Enqueued by <see cref="ReconciliationOrchestratorJob"/>; its only argument is an opaque tenant
/// identifier.
///
/// <para>
/// One <see cref="IntegrationReconciliationRun"/> per active core-banking connection, each
/// comparing the two sides the deployment actually has:
/// </para>
///
/// <list type="number">
/// <item><b>The CRM side is enumerated from <c>integration_reference</c></b>, not from M01. That
///   is what the criterion's "(avec <c>integration_reference</c>)" asks for and it is also the
///   only route that exists: <c>ICustomersModule</c> exposes no enumeration method at all — no
///   "list the active clients of this tenant" — and adding one would be the wrong contract
///   anyway, because it would hand back every customer of the tenant including the ones that have
///   no business with this connection. The reference table is scoped to a tenant AND a connection
///   and holds exactly the customers this connection has exchanged with. M01 is then consulted
///   for each one's status through the BATCH read.</item>
/// <item><b>The external side is the INT-21 read model</b>, <c>cbs_customer_snapshot</c>, whose
///   presence is the CBS's answer about existence and whose <c>KycLevelInCbs</c> is the tier it
///   holds.</item>
/// </list>
///
/// <para>
/// <b>Which gap types it can look for is not a detail of this job</b> — it is
/// <see cref="ReconciliationScope"/>, stamped onto every run row so a type that cannot fire never
/// reads as a type that fired and found nothing. <see cref="GapType.MissingInCrm"/> is in that
/// list; the argument is written out there.
/// </para>
///
/// <para>
/// ── <b>Relationship with INT-21's <c>CbsKycMismatchDetectedEvent</c></b> ─────────────────
/// </para>
///
/// <para>
/// Both mechanisms see the same disagreement about a KYC tier, and they are deliberately NOT the
/// same thing. <b>INT-21 is the notification; INT-34 is the ledger.</b>
/// </para>
///
/// <list type="bullet">
/// <item>INT-21 fires <i>at the moment a snapshot is refreshed</i> and names one customer: it is
///   how an officer learns promptly that a tier moved. It has no state, cannot be resolved, and
///   says nothing tomorrow.</item>
/// <item>INT-34 writes a dated, de-duplicated, resolvable row that answers "is it still true
///   today, how long has it been open, who closed it and why". That is what an inspection asks
///   for and what an event cannot be.</item>
/// </list>
///
/// <para>
/// <b>The gap is derived from the STATE, never from the event</b> — and that is the load-bearing
/// decision. Consuming <c>CbsKycMismatchDetectedEvent</c> to write the gap would have been less
/// code and would have broken criterion 3: nothing ever publishes "it stopped diverging", so an
/// event-fed ledger could open gaps and never close one. Auto-close only exists because the
/// comparison is re-run from scratch against current state every night.
/// </para>
///
/// <para>
/// <b>Nothing is therefore reported twice to the same reader.</b> This job publishes no
/// <c>CbsKycMismatchDetectedEvent</c> and the projector writes no gap; the two paths never cross.
/// What an administrator receives from INT-34 is one <c>ReconciliationCompletedEvent</c> per
/// connection per day carrying COUNTS — never a per-customer message — so the per-customer story
/// stays INT-21's and the ledger total stays this one's. And because the comparison reuses
/// <c>SnapshotKycLevels.Diverge</c>, the two mechanisms cannot disagree about what a divergence
/// is: there is one definition of a tier comparison in the platform.
/// </para>
///
/// <para>
/// <b>On the named queue <c>integration-sync</c></b>, like <see cref="KycLimits.KycLimitWatchJob"/>
/// which it is modelled on: the comparison reads the read model and M01/M02 and makes no outbound
/// call at all, so it has no business competing for the pool rationed by a CBS licence. Not
/// <c>integration-write</c>, which exists so a short outbound call an agent is waiting on never
/// queues behind a long sweep. <b>The host must declare that queue</b> — setting
/// <c>BackgroundJobServerOptions.Queues</c> REPLACES the default set, so a name missing from it is
/// enqueued successfully and never processed, with nothing in the logs.
/// </para>
/// </summary>
[Queue(QueueName)]
public sealed class ReconcileTenantJob(IServiceScopeFactory scopeFactory)
{
    /// <summary>
    /// The read queue of the module, declared by <c>DispatchServiceRegistration.Queues</c> and by
    /// the host's <c>BackgroundJobServerOptions</c>. Shared with the sync sweeps and the ceiling
    /// watch on purpose — see the type remarks.
    /// </summary>
    public const string QueueName = "integration-sync";

    /// <summary>
    /// References compared per page, and therefore the size of the single SQL <c>IN</c> handed to
    /// <c>ICustomersModule.GetClientSummariesAsync</c>.
    ///
    /// <para>
    /// That contract is explicit that a caller "must keep the batch bounded to a page's worth of
    /// ids: the implementation turns them into a single SQL <c>IN</c>, which stops being a favour
    /// somewhere in the thousands" — so a tenant with fifty thousand references must not become
    /// one statement with fifty thousand parameters. 200 is comfortably inside every driver's
    /// parameter limit and inside the planner's range for an <c>IN</c>, and it is also the amount
    /// this job holds in memory at once: one page of references, their summaries and their
    /// snapshot rows, and nothing else.
    /// </para>
    /// </summary>
    internal const int PageSize = 200;

    public async Task ExecuteAsync(Guid tenantId)
    {
        // Set BEFORE the scope is created. ITenantContext and ICurrentUser are built from this
        // ambient context when the scope resolves them, and a scope opened first would capture the
        // HTTP implementations and find no request — same comment, same reason, as
        // KycLimitWatchJob. The actor is SYSTEM: a nightly comparison found the divergence, no
        // agent did.
        using var bgCtx = BackgroundJobContext.SetScope(tenantId, Guid.Empty, "SYSTEM");
        using var scope = scopeFactory.CreateScope();

        var sp = scope.ServiceProvider;
        var logger = sp.GetRequiredService<ILogger<ReconcileTenantJob>>();

        var report = await RunAsync(
            sp.GetRequiredService<IntegrationDbContext>(),
            sp.GetRequiredService<ICustomersModule>(),
            sp.GetRequiredService<IKycModule>(),
            // Keyed per module (key = DbContext type name), the repo-wide outbox convention. The
            // constructor-injection spelling is [FromKeyedServices(nameof(IntegrationDbContext))];
            // a job that opens its own scope resolves the same registration by hand.
            sp.GetRequiredKeyedService<IEventPublisher>(nameof(IntegrationDbContext)),
            sp.GetRequiredService<TimeProvider>(),
            logger,
            tenantId,
            CancellationToken.None);

        logger.LogInformation(
            "Reconciliation of tenant {TenantId}: {Connections} connection(s), {Checked} "
            + "reference(s) compared, {Observed} divergence(s) observed ({New} new), {Closed} "
            + "closed automatically, {Unknown} reference(s) whose CRM record M01 does not know.",
            tenantId, report.Connections, report.Checked, report.Observed, report.New,
            report.Closed, report.UnknownInCrm);
    }

    /// <summary>
    /// The sweep itself, collaborators passed in rather than resolved — so a test pins the rules
    /// without a DI container, the split every job in this module uses.
    ///
    /// <para>
    /// <b>One connection's failure does not end the tenant's sweep.</b> Each connection is
    /// reconciled in its own try and its own run row; a connection whose comparison throws is
    /// recorded as a FAILED run, which is itself a finding an inspection asks about, and the other
    /// connections still get their night's comparison.
    /// </para>
    /// </summary>
    internal static async Task<ReconciliationReport> RunAsync(
        IntegrationDbContext db,
        ICustomersModule customers,
        IKycModule kyc,
        IEventPublisher publisher,
        TimeProvider clock,
        ILogger logger,
        Guid tenantId,
        CancellationToken ct)
    {
        var report = new ReconciliationReport();

        // IgnoreQueryFilters paired with an explicit tenant predicate, as every job in this repo
        // does: a job runs outside any HTTP request and the ambient tenant is not necessarily the
        // one being swept.
        //
        // CORE BANKING only, and active only. The external side of this comparison is
        // cbs_customer_snapshot, which no insurance connection writes — reconciling one would
        // report its whole portfolio as MissingInExternal on the strength of a read model that was
        // never meant to describe it. A deactivated connection is not compared either: its
        // references are frozen history, and the snapshot behind it stopped being refreshed.
        var connections = await db.Connections
            .IgnoreQueryFilters()
            .Where(c => c.TenantId == tenantId
                     && c.IsActive
                     && c.Family == IntegrationFamily.CoreBanking)
            .OrderBy(c => c.Id)
            .Select(c => c.Id)
            .ToListAsync(ct);

        foreach (var connectionId in connections)
        {
            report.Connections++;

            try
            {
                var one = await ReconcileConnectionAsync(
                    db, customers, kyc, publisher, clock, logger, tenantId, connectionId, ct);

                report.Add(one);
            }
            catch (Exception ex)
            {
                report.Failed++;

                logger.LogError(
                    ex,
                    "Reconciliation of connection {ConnectionId} of tenant {TenantId} failed; the "
                    + "run row records the failure and the other connections are unaffected.",
                    connectionId, tenantId);
            }
        }

        return report;
    }

    /// <summary>
    /// One connection's comparison, in one run row and one transaction.
    ///
    /// <para>
    /// <b>The run row is written FIRST and committed on its own.</b> That is what makes a crashed
    /// comparison visible: <see cref="IntegrationReconciliationRun"/> exists so the control
    /// function can show the comparison ran every day, and a run that died must leave a row
    /// saying so rather than leaving the night looking like it never happened.
    /// </para>
    ///
    /// <para>
    /// <b>Everything after it commits ONCE.</b> The gap rows, the run's counters and the outbox
    /// row of the summary go in a single <c>SaveChangesAsync</c> — the outbox publisher writes
    /// into this same context without saving — so the summary can never describe a ledger that was
    /// rolled back, nor be lost while the gaps stand. The number of entities that accumulates is
    /// the number of DIVERGENCES, not of customers: the references, summaries and snapshots are
    /// read as projections and never tracked.
    /// </para>
    /// </summary>
    private static async Task<ReconciliationReport> ReconcileConnectionAsync(
        IntegrationDbContext db,
        ICustomersModule customers,
        IKycModule kyc,
        IEventPublisher publisher,
        TimeProvider clock,
        ILogger logger,
        Guid tenantId,
        Guid connectionId,
        CancellationToken ct)
    {
        var run = IntegrationReconciliationRun.Start(
            tenantId, connectionId, clock, ReconciliationScope.NotDetected);

        db.ReconciliationRuns.Add(run);
        await db.SaveChangesAsync(ct);

        var report = new ReconciliationReport { Connections = 1 };

        try
        {
            var gapsByType = await ComparePortfolioAsync(
                db, customers, kyc, clock, logger, tenantId, connectionId, run.Id, report, ct);

            run.Finish(report.Checked, report.Observed, report.Closed, clock);

            // ── Criterion 4 — only when there are NEW gaps ──────────────────
            // "lorsqu'il y a de nouveaux écarts". A summary sent every night regardless would be
            // a mail nobody opens by the end of the month, and the one night it matters would look
            // exactly like the twenty-nine before it. A still-open gap is already in the ledger
            // and already has an owner.
            if (report.New > 0)
            {
                // Through the outbox, in the run's own transaction. COUNTS and nothing else: the
                // detail stays behind Integration.Reconciliation.View, and a notification that
                // named customers would put identity data in an e-mail body.
                await publisher.PublishAsync(
                    new ReconciliationCompletedEvent(
                        TenantId: tenantId,
                        ConnectionId: connectionId,
                        RunId: run.Id,
                        CheckedCount: report.Checked,
                        NewGapCount: report.New,
                        ClosedGapCount: report.Closed,
                        GapsByType: gapsByType,
                        FinishedAt: clock.GetUtcNow()),
                    ct);
            }

            await db.SaveChangesAsync(ct);

            return report;
        }
        catch (Exception)
        {
            // Drop whatever the failed comparison had half-built — gap rows still Added, a touched
            // gap's new LastSeenAt — so the Fail below is the only thing this transaction carries.
            // Without the clear, saving the failure would also save a partial ledger.
            db.ChangeTracker.Clear();

            var crashed = await db.ReconciliationRuns
                .IgnoreQueryFilters()
                .AsTracking()
                .FirstOrDefaultAsync(r => r.Id == run.Id, ct);

            if (crashed is not null)
            {
                // The type and message only. A reconciliation failure detail is read by an
                // operator and may name a connection, never a customer or a payload.
                crashed.Fail(
                    $"The comparison was abandoned after {report.Checked} reference(s).", clock);

                await db.SaveChangesAsync(ct);
            }

            throw;
        }
    }

    /// <summary>
    /// Walks the connection's customer references page by page, records what diverges, and closes
    /// what no longer does. Returns the per-type counts of the gaps it OPENED.
    ///
    /// <para>
    /// <b>Keyset paging on <c>external_id</c>, not <c>Skip</c>/<c>Take</c>.</b> Offset paging over
    /// a table a live platform is inserting into can skip a row or hand one back twice at a page
    /// boundary, and a reconciliation that silently skips references is the one bug this job must
    /// not have. <c>external_id</c> is the cursor because
    /// <c>ux_integration_reference_external (tenant_id, connection_id, entity_type, external_id)</c>
    /// makes the order both UNIQUE — so no boundary can be ambiguous — and index-backed, so each
    /// page is a range scan rather than a sort. The snapshot side is read for exactly the ids of
    /// the page, which is the same keyset seen from the other table: neither side is ever held
    /// whole in memory.
    /// </para>
    /// </summary>
    private static async Task<Dictionary<string, int>> ComparePortfolioAsync(
        IntegrationDbContext db,
        ICustomersModule customers,
        IKycModule kyc,
        TimeProvider clock,
        ILogger logger,
        Guid tenantId,
        Guid connectionId,
        Guid runId,
        ReconciliationReport report,
        CancellationToken ct)
    {
        var opened = new Dictionary<string, int>(StringComparer.Ordinal);

        // ── The open ledger, read once ──────────────────────────────────────
        // Once rather than per page, because this set is BOTH halves of criterion 3: the dedup
        // ("is this gap already open?") and the auto-close ("which open gaps did today's
        // comparison not see?"). It is small by construction — it is the backlog a human is
        // working through, not a history.
        var openGaps = await db.ReconciliationGaps
            .IgnoreQueryFilters()
            .AsTracking()
            .Where(g => g.TenantId == tenantId
                     && g.ConnectionId == connectionId
                     && g.Resolution == GapResolution.Open)
            .ToListAsync(ct);

        var byKey = new Dictionary<GapKey, IntegrationReconciliationGap>();

        foreach (var gap in openGaps)
        {
            // Indexer assignment, not ToDictionary, and it stays that way even though the index
            // now forbids the duplicate it tolerates.
            //
            // ux_integration_reconciliation_gap_open covers (tenant, connection, type, crm_id,
            // external_id), and PostgreSQL treats NULLs as DISTINCT by default — so for the two
            // types that leave one of those columns null by contract (MissingInExternal has no
            // external id, MissingInCrm no CRM id) the index constrained nothing and two identical
            // open rows were accepted. That was found while reviewing this job and fixed at the
            // source: the index now carries NULLS NOT DISTINCT, verified on PostgreSQL 18 against
            // the real schema.
            //
            // This line is kept as the cheaper half of the guarantee: the dedup is read-set based
            // and does not lean on the index, which is what lets it REPAIR a pre-existing
            // duplicate pair rather than preserve it — only one row of the pair is touched, so the
            // other falls into the auto-close set below and the table converges on its own.
            byKey[new GapKey(gap.GapType, gap.CrmId, gap.ExternalId)] = gap;
        }

        // Candidates for automatic closure: the open gaps of the types THIS run can look for.
        // Filtering by scope is not defensive decoration — a run must never close a gap of a type
        // it never scanned, or the first night after deployment would silently close every open
        // MissingInCrm row on the grounds that it did not find one.
        var unseen = openGaps
            .Where(g => ReconciliationScope.Detected.Contains(g.GapType))
            .Select(g => g.Id)
            .ToHashSet();

        string? cursor = null;

        while (true)
        {
            var query = db.References
                .IgnoreQueryFilters()
                .Where(r => r.TenantId == tenantId
                         && r.ConnectionId == connectionId
                         && r.EntityType == IntegrationEntityTypes.Customer);

            if (cursor is not null)
            {
                // CA1309 asks for StringComparison.Ordinal here and it would be WRONG, which is
                // why it is suppressed rather than satisfied. This expression is translated to SQL
                // (`external_id > @cursor`) and therefore evaluated under the COLUMN's collation —
                // the very same collation the OrderBy below sorts under. A keyset cursor is only
                // sound while its predicate and its ordering agree, and pinning the comparison to
                // .NET ordinal semantics would make them disagree on any non-C collation: pages
                // would silently skip references, which is the one failure this job must not have.
#pragma warning disable CA1309
                query = query.Where(r => string.Compare(r.ExternalId, cursor) > 0);
#pragma warning restore CA1309
            }

            var page = await query
                .OrderBy(r => r.ExternalId)
                .Take(PageSize)
                .Select(r => new ReferencedCustomer(r.CrmId, r.ExternalId))
                .ToListAsync(ct);

            if (page.Count == 0) break;

            cursor = page[^1].ExternalId;

            // A List and never an array: on .NET 10 an array's Contains binds to the
            // ReadOnlySpan<T> extension and stops translating, which would turn the snapshot read
            // into a client-side evaluation over the tenant's whole read model.
            var crmIds = page.Select(p => p.CrmId).Distinct().ToList();

            var summaries = await customers.GetClientSummariesAsync(tenantId, crmIds, ct);

            // Keyed by (tenant, crm_customer_id) and deliberately NOT narrowed by connection,
            // even though the row carries one. A tenant has one CBS, so the read model is per
            // tenant; filtering on the connection would make the morning after an administrator
            // re-points or replaces a connection look like an entire portfolio vanishing from the
            // external system — tens of thousands of false MissingInExternal gaps.
            var snapshots = await db.CbsSnapshots
                .IgnoreQueryFilters()
                .Where(s => s.TenantId == tenantId && crmIds.Contains(s.CrmCustomerId))
                .Select(s => new SnapshotFacts(s.CrmCustomerId, s.KycLevelInCbs))
                .ToListAsync(ct);

            var snapshotByCustomer = snapshots.ToDictionary(s => s.CrmCustomerId);

            foreach (var reference in page)
            {
                report.Checked++;

                if (!summaries.TryGetValue(reference.CrmId, out var summary))
                {
                    // A reference whose CRM record M01 does not know. Counted and logged, never
                    // turned into a gap — ReconciliationScope explains why this is not
                    // MissingInCrm. M01 deletes no client (archiving is a status, anonymisation
                    // blanks fields), so this is a data-integrity anomaly rather than a
                    // reconciliation finding.
                    report.UnknownInCrm++;

                    logger.LogWarning(
                        "Reconciliation: connection {ConnectionId} of tenant {TenantId} holds a "
                        + "reference for CRM customer {CrmCustomerId}, which M01 does not know. "
                        + "No gap is opened; the reference table and M01 disagree.",
                        connectionId, tenantId, reference.CrmId);

                    continue;
                }

                var hasSnapshot = snapshotByCustomer.TryGetValue(reference.CrmId, out var facts);

                // M02 is asked only when the tier can actually matter — the customer is active in
                // the CRM and the external system holds it. One call per such customer, the same
                // cost KycLimitWatchJob already pays on the same population; IKycModule exposes no
                // batch read of limits.
                KycLevel? crmLevel = null;

                if (hasSnapshot && ReconciliationComparison.IsActiveInCrm(summary))
                    crmLevel = await ReadCrmKycLevelAsync(
                        kyc, logger, tenantId, reference.CrmId, ct);

                var divergence = ReconciliationComparison.Compare(
                    summary, hasSnapshot, crmLevel, facts?.KycLevelInCbs);

                if (divergence is null) continue;

                report.Observed++;

                // MissingInExternal carries NO external id, per the aggregate's own field
                // contract ("Null for GapType.MissingInExternal"). Nothing is lost by it: the gap
                // points at the CRM customer, and GET integration/references answers the external
                // identifier for anyone holding the permission — whereas an external customer
                // number on a row that gets exported is an identifier of a person at a bank.
                var externalId = divergence.GapType == GapType.MissingInExternal
                    ? null
                    : reference.ExternalId;

                var key = new GapKey(divergence.GapType, reference.CrmId, externalId);

                if (byKey.TryGetValue(key, out var existing))
                {
                    // Criterion 3, first half: still true today, so the SAME row is kept. Touch
                    // moves LastSeenAt and deliberately leaves DetectedAt alone — that date is the
                    // age of the problem, which is the figure an inspection asks for.
                    existing.Touch(clock);
                    unseen.Remove(existing.Id);
                    report.Recurring++;
                    continue;
                }

                // runId is the run that FIRST saw it, and later runs deliberately do not update
                // it — the aggregate says so, and it is what lets a report trace a finding back to
                // the night it appeared.
                db.ReconciliationGaps.Add(IntegrationReconciliationGap.Open(
                    tenantId: tenantId,
                    runId: runId,
                    connectionId: connectionId,
                    gapType: divergence.GapType,
                    crmId: reference.CrmId,
                    externalId: externalId,
                    clock: clock,
                    detailsJson: divergence.DetailsJson));

                report.New++;

                var typeName = divergence.GapType.ToString();
                opened[typeName] = opened.TryGetValue(typeName, out var count) ? count + 1 : 1;
            }

            // A short page is the last page: the cursor is unique, so there is nothing after it.
            if (page.Count < PageSize) break;
        }

        // ── Criterion 3, second half: what disappeared ──────────────────────
        // Closed by the SYSTEM, with no actor and no note: nobody decided it, it stopped being
        // true. Resolve() is the human path and demands a note; CloseAutomatically() is this one
        // and deliberately takes neither.
        foreach (var gap in openGaps.Where(g => unseen.Contains(g.Id)))
        {
            gap.CloseAutomatically(clock);
            report.Closed++;
        }

        return opened;
    }

    /// <summary>
    /// The tier M02 believes, in this module's vocabulary — or <c>null</c> when M02 could not be
    /// asked.
    ///
    /// <para>
    /// A throw is logged at warning and folded into the same null, the conservative side and the
    /// same choice <c>KycLimitWatchJob</c> makes: the alternative is a comparison that stops at
    /// the first customer whose KYC file M02 trips over, leaving every reference after it in the
    /// page unreconciled — and a silently truncated reconciliation looks exactly like a clean one.
    /// Null skips the KYC comparison for that customer; it never produces a gap.
    /// </para>
    /// </summary>
    private static async Task<KycLevel?> ReadCrmKycLevelAsync(
        IKycModule kyc, ILogger logger, Guid tenantId, Guid crmCustomerId, CancellationToken ct)
    {
        try
        {
            var limits = await kyc.GetLimitsAsync(tenantId, crmCustomerId, ct);

            // FromLimits, not a comparison written here: SnapshotKycLevels is where M02's
            // vocabulary becomes this module's, and null limits legitimately mean KycLevel.None
            // ("no file at all" must never read as "unrestricted").
            return SnapshotKycLevels.FromLimits(limits);
        }
        catch (Exception ex)
        {
            logger.LogWarning(
                ex,
                "Reconciliation skipped the KYC tier of customer {CrmCustomerId} of tenant "
                + "{TenantId}: M02 could not answer. No divergence is assumed.",
                crmCustomerId, tenantId);

            return null;
        }
    }

    /// <summary>One reference, projected to the two values the comparison needs.</summary>
    private sealed record ReferencedCustomer(Guid CrmId, string ExternalId);

    /// <summary>
    /// A snapshot projected to what the comparison needs: the customer reference and the tier the
    /// CBS holds. Deliberately no balances, no accounts, no loans — the comparison has no use for
    /// a customer's financial position and a reconciliation report must not carry one.
    /// </summary>
    private sealed record SnapshotFacts(Guid CrmCustomerId, KycLevel? KycLevelInCbs);

    /// <summary>
    /// The dedup key of the aggregate's own doc comment: <c>(type, crm_id, external_id)</c> among
    /// the OPEN rows of one connection. A record struct so the dictionary compares by value,
    /// nulls included.
    /// </summary>
    private readonly record struct GapKey(GapType GapType, Guid? CrmId, string? ExternalId);
}

/// <summary>What one reconciliation did, for the log line and for the tests.</summary>
internal sealed class ReconciliationReport
{
    /// <summary>Active core-banking connections reconciled.</summary>
    public int Connections { get; set; }

    /// <summary>Connections whose comparison threw and were recorded as a failed run.</summary>
    public int Failed { get; set; }

    /// <summary>References compared.</summary>
    public int Checked { get; set; }

    /// <summary>Divergences seen — <see cref="New"/> plus <see cref="Recurring"/>.</summary>
    public int Observed { get; set; }

    /// <summary>Gaps opened by this run. The figure criterion 4's notification depends on.</summary>
    public int New { get; set; }

    /// <summary>Gaps that were already open and are still true: the SAME row, touched.</summary>
    public int Recurring { get; set; }

    /// <summary>Open gaps this run no longer found, closed automatically.</summary>
    public int Closed { get; set; }

    /// <summary>References whose CRM record M01 does not know. Logged, never a gap.</summary>
    public int UnknownInCrm { get; set; }

    public void Add(ReconciliationReport other)
    {
        ArgumentNullException.ThrowIfNull(other);

        Checked += other.Checked;
        Observed += other.Observed;
        New += other.New;
        Recurring += other.Recurring;
        Closed += other.Closed;
        UnknownInCrm += other.UnknownInCrm;
    }
}
