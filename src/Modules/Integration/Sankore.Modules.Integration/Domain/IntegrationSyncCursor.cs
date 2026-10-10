namespace Sankore.Modules.Integration.Domain;

using Sankore.Shared.Kernel;

/// <summary>
/// How far one stream of one connection has been synchronised (INT-20).
///
/// <para>
/// The cursor advances only AFTER the commit of the data it describes. The opposite order — save
/// the cursor, then write the rows — loses a window of records on any crash, silently: nothing
/// afterwards knows they were skipped, because the cursor says they were done.
/// </para>
///
/// <para>
/// Composite primary key <c>(tenant_id, connection_id, stream)</c>, as specified: there is
/// exactly one cursor per triple and a surrogate id would permit two.
/// </para>
/// </summary>
public sealed class IntegrationSyncCursor : AggregateRoot
{
    public Guid ConnectionId { get; private set; }

    public SyncStream Stream { get; private set; }

    /// <summary>
    /// Opaque to this module: a timestamp for one system, an opaque page token for another. It
    /// is handed back to the adapter that produced it and interpreted by nobody else.
    /// </summary>
    public string? Cursor { get; private set; }

    public DateTimeOffset? LastRunAt { get; private set; }

    /// <summary>
    /// Last run that actually committed. Kept apart from <see cref="LastRunAt"/> because "it ran
    /// an hour ago" and "it last succeeded in March" are different operational facts, and a
    /// single column would show a healthy-looking job that has been failing for weeks.
    /// </summary>
    public DateTimeOffset? LastSuccessAt { get; private set; }

    public int ConsecutiveFailures { get; private set; }

    /// <summary>Last failure, operator-facing. Never a payload.</summary>
    public string? LastError { get; private set; }

    public uint Version { get; private set; }

    private IntegrationSyncCursor() { }

    public static IntegrationSyncCursor Create(Guid tenantId, Guid connectionId, SyncStream stream)
    {
        if (tenantId == Guid.Empty) throw new DomainException("TenantId is required.");
        if (connectionId == Guid.Empty) throw new DomainException("ConnectionId is required.");

        return new IntegrationSyncCursor
        {
            TenantId = tenantId,
            ConnectionId = connectionId,
            Stream = stream,
        };
    }

    /// <summary>Called before the work, so a crashed run is still visible as an attempt.</summary>
    public void BeginRun(TimeProvider clock) => LastRunAt = clock.GetUtcNow();

    /// <summary>Called AFTER the data commit. Advancing it earlier loses records.</summary>
    public void Advance(string? cursor, TimeProvider clock)
    {
        Cursor = cursor;
        LastSuccessAt = clock.GetUtcNow();
        ConsecutiveFailures = 0;
        LastError = null;
    }

    public void RecordFailure(string error, TimeProvider clock)
    {
        ConsecutiveFailures++;
        LastError = error.Length <= 1000 ? error : error[..1000];
        LastRunAt = clock.GetUtcNow();
    }
}
