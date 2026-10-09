namespace Sankore.Modules.Integration.Features.Sync;

using System.Globalization;
using Hangfire;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Sankore.Modules.Integration.Domain;
using Sankore.Modules.Integration.Features.Snapshot;
using Sankore.Modules.Integration.Infrastructure;
using Sankore.Shared.Infrastructure.BackgroundJobs;

/// <summary>
/// Hangfire job — the incremental sweep of ONE stream of ONE connection (INT-20, criteria 2, 3
/// and 5). Enqueued by <see cref="IntegrationSyncOrchestrator"/>.
///
/// <para>
/// <b>On the named queue <c>integration-sync</c></b> (criterion 5). A sweep reads from a bank with
/// a per-minute licence ceiling and must not share a worker pool with a four-hundred-row lead
/// import; an operator must also be able to drain it on its own.
/// <see cref="SyncServiceRegistration"/> carries the exact <c>BackgroundJobServerOptions</c> the
/// host has to declare for this attribute to mean anything — <b>without it the job is enqueued to a
/// queue no worker reads and never runs at all</b>.
/// </para>
///
/// <para>
/// <b>It projects and it does not write the snapshot.</b> The whole body of per-customer work is
/// <see cref="ICbsSnapshotProjector.ProjectAsync"/>, which INT-21 owns. This job owns the three
/// things the projection cannot: the cursor's ordering guarantee, the restriction to known
/// customers, and the failure accounting.
/// </para>
/// </summary>
[Queue(QueueName)]
public sealed class IntegrationSyncJob(IServiceScopeFactory scopeFactory)
{
    /// <summary>
    /// The sync queue. Declared once here and referenced by <see cref="SyncCustomerJob"/> and by
    /// <see cref="SyncServiceRegistration"/>'s host-wiring instructions.
    /// </summary>
    public const string QueueName = "integration-sync";

    public async Task ExecuteAsync(Guid tenantId, Guid connectionId, SyncStream stream)
    {
        // Set BEFORE the scope is created. ICurrentUser and ITenantContext are built from this
        // ambient context when the scope resolves them, and a scope opened first would capture the
        // HTTP implementations and find no request — the precedent, with the same comment, is
        // DispatchTenantCommandsJob. The actor is SYSTEM: nobody asked for this sweep, an interval
        // elapsed.
        using var bgCtx = BackgroundJobContext.SetScope(tenantId, Guid.Empty, "SYSTEM");
        using var scope = scopeFactory.CreateScope();

        var sp = scope.ServiceProvider;
        var logger = sp.GetRequiredService<ILogger<IntegrationSyncJob>>();

        var report = await RunAsync(
            sp.GetRequiredService<IntegrationDbContext>(),
            // GetRequiredService and not GetService: an unregistered projector must throw here,
            // on the first run, rather than let the sweep walk every customer and project nothing
            // while reporting success and advancing the cursor past data it never read.
            sp.GetRequiredService<ICbsSnapshotProjector>(),
            sp.GetRequiredService<TimeProvider>(),
            logger,
            tenantId,
            connectionId,
            stream,
            CancellationToken.None);

        logger.LogInformation(
            "Integration sync {Stream} for tenant {TenantId} connection {ConnectionId}: "
            + "{Projected} customer(s) projected, skipped={Skipped}, failed={Failed}.",
            stream, tenantId, connectionId, report.Projected, report.Skipped, report.Failed);
    }

    /// <summary>
    /// The sweep itself, with its collaborators passed in rather than resolved — so the ordering
    /// guarantee below can be pinned by a test without a DI container or a Hangfire storage.
    ///
    /// <para>
    /// <b>Criterion 2 — the cursor advances only after the commit of the data it describes.</b>
    /// The order is, exactly:
    /// </para>
    /// <list type="number">
    ///   <item><c>BeginRun</c> + <c>SaveChangesAsync</c> — the attempt is visible before any work,
    ///     which is also what makes the row the orchestrator's in-flight lock.</item>
    ///   <item>the projection of every referenced customer.</item>
    ///   <item><c>SaveChangesAsync</c> — the DATA commit.</item>
    ///   <item><c>Advance</c> + <c>SaveChangesAsync</c> — a SECOND transaction, strictly after.</item>
    /// </list>
    /// <para>
    /// Two commits and not one, on purpose. The opposite order — or one transaction that saves the
    /// cursor alongside a partially projected sweep — loses a window of records on any crash, and
    /// loses it <i>silently</i>: nothing afterwards knows they were skipped, because the cursor
    /// says they were done. A crash between step 3 and step 4 re-reads a window that was already
    /// projected, which is the cheap direction of the trade: the projection is idempotent per
    /// customer, a skipped window is not recoverable.
    /// </para>
    ///
    /// <para>
    /// <b>The cursor is advanced to the instant the run STARTED</b>, not to the instant it
    /// finished. Anything that changed in the external system while the run was walking must be
    /// re-read by the next one; a cursor set to the end instant would step over exactly those
    /// records. The value is an ISO-8601 round-trip string under
    /// <see cref="CultureInfo.InvariantCulture"/> — <c>IntegrationSyncCursor.Cursor</c> is opaque
    /// to this module, and a timestamp rendered with the process culture is a cursor that changes
    /// meaning when the container's locale does.
    /// </para>
    ///
    /// <para>
    /// <b>Criterion 3 — only customers present in <c>integration_reference</c> are
    /// synchronised.</b> The set comes from that table filtered on this connection and
    /// <c>EntityType == Customer</c>, and from nowhere else. It is not a performance filter: a
    /// reference row is written by INT-07 in the same transaction as the write that created the
    /// customer on the other side, so it is the only record that this tenant's CRM customer and
    /// that external identity are the same person. A sweep that went by anything else — a list the
    /// external system offers, an id in a webhook body — would project one tenant's data onto
    /// another's customer, or invent a customer we have no mandate over.
    /// </para>
    ///
    /// <para>
    /// <b>Criterion 5 — a failing run does not block the others.</b> The whole body is wrapped:
    /// the error is logged, <c>RecordFailure</c> stores an operator-facing message, and the cursor
    /// keeps the value it had. Nothing propagates out of this method, so neither the other streams
    /// of this connection nor any other tenant's runs are affected — and the failure is still
    /// visible, in <c>ConsecutiveFailures</c> and <c>LastError</c>, which is what distinguishes a
    /// job that has been failing for weeks from one that merely has not run yet.
    /// </para>
    /// </summary>
    internal static async Task<SyncRunReport> RunAsync(
        IntegrationDbContext db,
        ICbsSnapshotProjector projector,
        TimeProvider clock,
        ILogger logger,
        Guid tenantId,
        Guid connectionId,
        SyncStream stream,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(projector);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(logger);

        var report = new SyncRunReport();
        var startedAt = clock.GetUtcNow();

        // The connection may have been deactivated between the fan-out and this run. Criterion 1
        // scopes the sweep to ACTIVE connections, so the check belongs at the point of work too —
        // and the cursor is left completely untouched, because a run that did not happen must not
        // look like one that did.
        var isActive = await db.Connections
            .IgnoreQueryFilters()
            .AnyAsync(c => c.TenantId == tenantId && c.Id == connectionId && c.IsActive, ct);

        if (!isActive)
        {
            report.Skipped = true;

            logger.LogInformation(
                "Integration sync {Stream}: connection {ConnectionId} of tenant {TenantId} is no "
                + "longer active; nothing swept and the cursor is left where it was.",
                stream, connectionId, tenantId);

            return report;
        }

        var cursor = await LoadOrCreateCursorAsync(db, tenantId, connectionId, stream, ct);

        cursor.BeginRun(clock);
        await db.SaveChangesAsync(ct);

        try
        {
            // AsTracking because each reference is stamped with MarkSynced below; the entity
            // carries no sensitive field, so loading it whole costs nothing a projection would
            // save. Ordered deterministically — CreatedAt alone is not a total order, two
            // references written in one transaction share it to the tick.
            var customers = await db.References
                .AsTracking()
                .IgnoreQueryFilters()
                .Where(r => r.TenantId == tenantId
                         && r.ConnectionId == connectionId
                         && r.EntityType == IntegrationEntityTypes.Customer)
                .OrderBy(r => r.CreatedAt)
                .ThenBy(r => r.Id)
                .ToListAsync(ct);

            foreach (var reference in customers)
            {
                await projector.ProjectAsync(tenantId, connectionId, reference.CrmId, ct);
                reference.MarkSynced(clock);
                report.Projected++;
            }

            // ── The DATA commit (step 3). ───────────────────────────────────
            await db.SaveChangesAsync(ct);

            // ── And only now the cursor (step 4), in its own transaction. ───
            cursor.Advance(startedAt.ToString("O", CultureInfo.InvariantCulture), clock);
            await db.SaveChangesAsync(ct);
        }
        catch (Exception ex)
        {
            report.Failed = true;

            logger.LogError(
                ex, "Integration sync {Stream} for tenant {TenantId} connection {ConnectionId} "
                  + "failed after {Projected} customer(s); the cursor is unchanged and the window "
                  + "will be re-read.", stream, tenantId, connectionId, report.Projected);

            // Everything the failed sweep had queued — the half-applied sync marks, an Advance
            // that may already have been staged — must not ride along into the failure write
            // below, which is its own transaction. Clearing is also what guarantees the cursor
            // keeps its committed value.
            db.ChangeTracker.Clear();

            try
            {
                await RecordFailureAsync(db, clock, tenantId, connectionId, stream, ex.Message, ct);
            }
            catch (Exception recordingEx)
            {
                // The store itself is refusing writes. The run is lost either way; the next one is
                // due one interval later and re-reads the same window, which is the behaviour this
                // ordering exists to guarantee.
                logger.LogError(
                    recordingEx,
                    "Recording the failure of integration sync {Stream} for tenant {TenantId} "
                    + "also failed.", stream, tenantId);
            }
        }

        return report;
    }

    /// <summary>
    /// The cursor for this triple, created on first use.
    ///
    /// <para>
    /// There is no seeder and there cannot usefully be one: a cursor belongs to a (tenant,
    /// connection, stream) and a connection is something an administrator creates at any time. The
    /// row appears the first time its stream is swept, which also means adding a stream to
    /// <see cref="SyncStream"/> needs no migration and no backfill.
    /// </para>
    /// </summary>
    private static async Task<IntegrationSyncCursor> LoadOrCreateCursorAsync(
        IntegrationDbContext db, Guid tenantId, Guid connectionId, SyncStream stream,
        CancellationToken ct)
    {
        var cursor = await db.SyncCursors
            .AsTracking()
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(
                c => c.TenantId == tenantId
                  && c.ConnectionId == connectionId
                  && c.Stream == stream, ct);

        if (cursor is not null) return cursor;

        cursor = IntegrationSyncCursor.Create(tenantId, connectionId, stream);
        db.SyncCursors.Add(cursor);

        return cursor;
    }

    /// <summary>
    /// Records a failed run against the COMMITTED cursor row, re-read after the change tracker was
    /// cleared. Re-reading rather than reusing the instance is the point: the cleared instance is
    /// detached, and attaching it would carry the staged <c>Advance</c> back in.
    /// </summary>
    private static async Task RecordFailureAsync(
        IntegrationDbContext db, TimeProvider clock, Guid tenantId, Guid connectionId,
        SyncStream stream, string error, CancellationToken ct)
    {
        var cursor = await db.SyncCursors
            .AsTracking()
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(
                c => c.TenantId == tenantId
                  && c.ConnectionId == connectionId
                  && c.Stream == stream, ct);

        // The first run of a stream can fail before its cursor was ever committed. Create the row
        // so the failure is recorded somewhere: a stream that has failed every time since it was
        // configured is otherwise indistinguishable from one that has never been due.
        if (cursor is null)
        {
            cursor = IntegrationSyncCursor.Create(tenantId, connectionId, stream);
            db.SyncCursors.Add(cursor);
        }

        cursor.RecordFailure(error, clock);
        await db.SaveChangesAsync(ct);
    }
}

/// <summary>What one sweep did, for the log line and for the tests.</summary>
internal sealed class SyncRunReport
{
    /// <summary>Customers handed to the projector. Only ever referenced customers.</summary>
    public int Projected { get; set; }

    /// <summary>The connection was deactivated between the fan-out and the run.</summary>
    public bool Skipped { get; set; }

    /// <summary>The sweep threw. The cursor kept its value and the error was recorded.</summary>
    public bool Failed { get; set; }
}
