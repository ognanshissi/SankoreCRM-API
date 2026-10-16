namespace Sankore.Modules.Integration.Features.Sync;

using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Integration.Domain;
using Sankore.Modules.Integration.Infrastructure;

/// <summary>
/// The two cursor operations every incremental sweep needs, in one place.
///
/// <para>
/// Extracted from <see cref="IntegrationSyncJob"/> when TEL-B-14 added a second sweep. The
/// ordering discipline those two share is subtle and load-bearing — the data commits, THEN the
/// cursor, in a separate transaction, and a failure re-reads rather than skips — so two copies of
/// the helpers around it would be two chances to get the failure path wrong in one of them. What
/// is NOT extracted is the ordering itself: each job states it, because each job is where the
/// reader needs to see it.
/// </para>
/// </summary>
internal static class SyncCursorGate
{
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
    internal static async Task<IntegrationSyncCursor> LoadOrCreateAsync(
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
    /// cleared.
    ///
    /// <para>
    /// Re-reading rather than reusing the instance is the point: the cleared instance is detached,
    /// and attaching it would carry the staged <c>Advance</c> back in — which is exactly how a
    /// failed sweep ends up advancing the cursor past data it never read.
    /// </para>
    /// </summary>
    internal static async Task RecordFailureAsync(
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
