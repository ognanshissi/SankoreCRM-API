namespace Sankore.Shared.Infrastructure.BackgroundJobs;

using Hangfire;
using Hangfire.Storage;

/// <summary>
/// Durable pause/resume for Hangfire recurring jobs.
///
/// Hangfire OSS has no notion of a paused recurring job — the dashboard can only
/// Remove one — and <c>Sankore.Api</c> re-registers every job with
/// <c>RecurringJob.AddOrUpdate</c> on each start, so a removal never survives a
/// restart. Pausing therefore snapshots the job's definition, removes it, and
/// records the id in a set; the API skips re-registering any id it finds there,
/// and <see cref="Resume"/> puts the job back exactly as it was.
///
/// State lives in Hangfire's own storage, so the dashboard host
/// (<c>Sankore.Hangfire</c>) and the job host (<c>Sankore.Api</c>) agree without a
/// second database or an extra migration.
/// </summary>
public sealed class RecurringJobPauseStore(JobStorage storage, IRecurringJobManager recurringJobs)
{
    /// <summary>Set holding the ids of every recurring job that is currently paused.</summary>
    public const string PausedSetKey = "sankore:paused-recurring-jobs";

    /// <summary>Hash holding one paused job's definition, copied verbatim from Hangfire's own hash.</summary>
    private const string SnapshotHashPrefix = "sankore:paused-recurring-job:";

    // Hangfire's internal storage layout for recurring jobs. Verified against
    // Hangfire 1.8.17: the set is scored by next execution, and each job's hash
    // carries Job (serialized InvocationData), Cron, TimeZoneId, Queue and Misfire.
    private const string HangfireSetKey = "recurring-jobs";
    private const string HangfireHashPrefix = "recurring-job:";

    // Serializes dashboard actions against each other. The API's startup guard
    // only reads the set, so it deliberately does not contend for this lock.
    private const string LockResource = "sankore:recurring-job-pause";
    private static readonly TimeSpan LockTimeout = TimeSpan.FromSeconds(15);

    /// <summary>Ids of every recurring job that is currently paused.</summary>
    public IReadOnlyCollection<string> GetPausedIds()
    {
        using var connection = storage.GetConnection();
        return ReadPausedIds(connection);
    }

    /// <summary>Ids of every recurring job Hangfire will schedule (i.e. not paused).</summary>
    public IReadOnlyCollection<string> GetActiveIds()
    {
        using var connection = storage.GetConnection();
        return connection.GetAllItemsFromSet(HangfireSetKey) ?? [];
    }

    public bool IsPaused(string recurringJobId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(recurringJobId);

        using var connection = storage.GetConnection();
        return ReadPausedIds(connection).Contains(recurringJobId);
    }

    /// <summary>
    /// The definition of a paused job, as snapshotted when it was paused, or
    /// <see langword="null"/> when the id is not paused or its snapshot is gone.
    /// </summary>
    public IReadOnlyDictionary<string, string>? GetSnapshot(string recurringJobId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(recurringJobId);

        using var connection = storage.GetConnection();
        var snapshot = connection.GetAllEntriesFromHash(SnapshotHashPrefix + recurringJobId);
        return snapshot is { Count: > 0 } ? snapshot : null;
    }

    /// <summary>
    /// Removes the recurring job from Hangfire's schedule and remembers its
    /// definition so <see cref="Resume"/> can put it back.
    /// </summary>
    /// <returns><see langword="false"/> when the job is already paused or unknown.</returns>
    public bool Pause(string recurringJobId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(recurringJobId);

        using var connection = storage.GetConnection();
        using var jobLock = connection.AcquireDistributedLock(LockResource, LockTimeout);

        if (ReadPausedIds(connection).Contains(recurringJobId))
        {
            return false;
        }

        // Snapshot BEFORE removing: RemoveIfExists deletes the very hash we
        // need to restore from.
        var definition = connection.GetAllEntriesFromHash(HangfireHashPrefix + recurringJobId);
        if (definition is not { Count: > 0 })
        {
            return false;
        }

        recurringJobs.RemoveIfExists(recurringJobId);

        using var transaction = connection.CreateWriteTransaction();
        transaction.AddToSet(PausedSetKey, recurringJobId);
        transaction.SetRangeInHash(SnapshotHashPrefix + recurringJobId, definition);
        transaction.Commit();

        return true;
    }

    /// <summary>
    /// Re-registers a paused job from its snapshot and clears the pause marker.
    /// </summary>
    /// <returns><see langword="false"/> when the job was not paused.</returns>
    public bool Resume(string recurringJobId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(recurringJobId);

        using var connection = storage.GetConnection();
        using var jobLock = connection.AcquireDistributedLock(LockResource, LockTimeout);

        if (!ReadPausedIds(connection).Contains(recurringJobId))
        {
            return false;
        }

        var snapshot = connection.GetAllEntriesFromHash(SnapshotHashPrefix + recurringJobId);

        if (snapshot is not null
            && snapshot.TryGetValue("Job", out var payload) && !string.IsNullOrWhiteSpace(payload)
            && snapshot.TryGetValue("Cron", out var cron) && !string.IsNullOrWhiteSpace(cron))
        {
            // The job's queue travels inside the serialized payload, so it survives
            // the round trip without being restored separately.
            var job = InvocationData.DeserializePayload(payload).DeserializeJob();
            recurringJobs.AddOrUpdate(recurringJobId, job, cron, BuildOptions(snapshot));
        }

        // No usable snapshot: still clear the marker, so the next Sankore.Api
        // start re-registers the job from code rather than leaving it stuck.
        using var transaction = connection.CreateWriteTransaction();
        transaction.RemoveFromSet(PausedSetKey, recurringJobId);
        transaction.RemoveHash(SnapshotHashPrefix + recurringJobId);
        transaction.Commit();

        return true;
    }

    /// <summary>Pauses every recurring job Hangfire currently schedules.</summary>
    /// <returns>How many jobs were actually paused.</returns>
    public int PauseAll()
    {
        var paused = 0;

        foreach (var id in GetActiveIds())
        {
            if (Pause(id))
            {
                paused++;
            }
        }

        return paused;
    }

    /// <summary>Resumes every paused recurring job.</summary>
    /// <returns>How many jobs were actually resumed.</returns>
    public int ResumeAll()
    {
        var resumed = 0;

        foreach (var id in GetPausedIds())
        {
            if (Resume(id))
            {
                resumed++;
            }
        }

        return resumed;
    }

    private static HashSet<string> ReadPausedIds(IStorageConnection connection)
        => connection.GetAllItemsFromSet(PausedSetKey) ?? [];

    private static RecurringJobOptions BuildOptions(Dictionary<string, string> snapshot)
    {
        var options = new RecurringJobOptions();

        if (snapshot.TryGetValue("TimeZoneId", out var timeZoneId) && !string.IsNullOrWhiteSpace(timeZoneId))
        {
            try
            {
                options.TimeZone = TimeZoneInfo.FindSystemTimeZoneById(timeZoneId);
            }
            catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
            {
                // The host no longer recognises the zone the job was registered with.
                // Falling back to the default beats refusing to resume the job.
            }
        }

        if (snapshot.TryGetValue("Misfire", out var misfire)
            && Enum.TryParse<MisfireHandlingMode>(misfire, out var mode))
        {
            options.MisfireHandling = mode;
        }

        return options;
    }
}
