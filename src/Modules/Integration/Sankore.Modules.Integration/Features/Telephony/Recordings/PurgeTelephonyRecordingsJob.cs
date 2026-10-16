namespace Sankore.Modules.Integration.Features.Telephony.Recordings;

using Hangfire;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Sankore.Modules.Integration.Domain;
using Sankore.Modules.Integration.Infrastructure;
using Sankore.Modules.Integration.Infrastructure.RecordingStorage;
using Sankore.Shared.Infrastructure.BackgroundJobs;

/// <summary>
/// Destroys the recordings whose retention has run out (TEL-B-17).
///
/// <para>
/// <b>The bytes go first, then the row says so.</b> That order is the one that cannot leave a
/// recording readable: a row marked purged whose object survived is a file nobody will ever look
/// for again — invisible to this job on its next run, invisible to the screens, and still on the
/// volume. The reverse — an object deleted whose row still says <c>Available</c> — is a player
/// that answers "not available", which is ugly and recoverable.
/// </para>
///
/// <para>
/// <b>A legal hold stops it, and the hold is checked by the AGGREGATE rather than by this job's
/// query.</b> <c>TelRecording.Purge</c> answers false for a held recording, so a hold placed
/// between the query and the delete is still honoured — which matters, because that is exactly
/// when it is placed: somebody has just been told a dispute is coming.
/// </para>
/// </summary>
[Queue(QueueName)]
public sealed class PurgeTelephonyRecordingsJob(IServiceScopeFactory scopeFactory)
{
    private const string QueueName = "integration-sync";

    /// <summary>
    /// How many recordings one run destroys. A ceiling so a tenant switching recording off, or a
    /// retention shortened from a year to a month, does not try to delete a hundred thousand
    /// objects in one Hangfire execution. The next run takes the rest; the sweep is daily and the
    /// backlog drains.
    /// </summary>
    internal const int MaxPerRun = 200;

    public async Task ExecuteAsync(Guid tenantId)
    {
        using var bgCtx = BackgroundJobContext.SetScope(tenantId, Guid.Empty, "SYSTEM");
        using var scope = scopeFactory.CreateScope();

        var sp = scope.ServiceProvider;
        var logger = sp.GetRequiredService<ILogger<PurgeTelephonyRecordingsJob>>();

        var purged = await RunAsync(
            sp.GetRequiredService<IntegrationDbContext>(),
            // GetService, like the fetch job: a tenant that never kept a recording has no store
            // and nothing to purge, and requiring one would make the sweep fail for every tenant
            // of a deployment that configured no key.
            sp.GetService<IRecordingStore>(),
            sp.GetRequiredService<TimeProvider>(),
            logger,
            tenantId,
            CancellationToken.None);

        if (purged > 0)
        {
            logger.LogInformation(
                "Telephony recordings: {Purged} purged for tenant {TenantId}.", purged, tenantId);
        }
    }

    /// <summary>
    /// Returns how many were destroyed, so a caller counts work rather than looking at a log.
    /// </summary>
    internal static async Task<int> RunAsync(
        IntegrationDbContext db,
        IRecordingStore? store,
        TimeProvider clock,
        ILogger logger,
        Guid tenantId,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(logger);

        if (store is null) return 0;

        var now = clock.GetUtcNow();

        // The index ix_tel_recording_due covers exactly this predicate, filtered on the same two
        // conditions — so the index stays the size of what is held rather than of what ever was.
        var due = await db.TelRecordings
            .IgnoreQueryFilters()
            .AsTracking()
            .Where(r => r.TenantId == tenantId
                     && r.Status == TelRecordingStatus.Available
                     && !r.LegalHold
                     && r.RetentionUntil <= now)
            .OrderBy(r => r.RetentionUntil)
            .Take(MaxPerRun)
            .ToListAsync(ct);

        var purged = 0;

        foreach (var recording in due)
        {
            if (recording.StorageRef is not { } storageRef)
            {
                // Available with no reference is a state nothing writes. Closed rather than left
                // to be re-examined every night for ever.
                recording.Purge(now);
                purged++;

                continue;
            }

            try
            {
                // The BYTES first. A false answer — the object was already gone — is not a
                // failure: the row is what has to catch up with the volume.
                await store.DeleteAsync(tenantId, storageRef, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // The row is left alone, so the next run tries again. Marking it purged here
                // would be the one mistake this job must not make: a file nobody will look for
                // again, still readable on the volume.
                logger.LogError(
                    ex, "Could not delete the object of recording {RecordingId} (tenant "
                      + "{TenantId}); the row is unchanged and the purge will retry.",
                    recording.Id, tenantId);

                continue;
            }

            // The aggregate decides, so a hold placed since the query is still honoured.
            if (recording.Purge(now)) purged++;
        }

        // The call rows follow: a purged recording leaves a call that must not advertise a player.
        if (purged > 0)
        {
            var purgedCallIds = due
                .Where(r => r.Status == TelRecordingStatus.Purged)
                .Select(r => r.CallId)
                .ToList();

            var calls = await db.TelCalls
                .IgnoreQueryFilters()
                .AsTracking()
                .Where(c => c.TenantId == tenantId && purgedCallIds.Contains(c.Id))
                .ToListAsync(ct);

            foreach (var call in calls)
                call.SetRecordingStatus(TelRecordingStatus.Purged);

            await db.SaveChangesAsync(ct);
        }

        return purged;
    }
}
