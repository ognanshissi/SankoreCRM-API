namespace Sankore.Shared.Infrastructure.Retention;

/// <summary>
/// One kind of technical data a module lets go of — processed messages, dead sessions, read
/// notifications, the files an import left behind. Not business data: those follow their own
/// retention rules (leads, clients, KYC) and are never erased without a person deciding.
///
/// <para>
/// Each module registers its purges as <c>ITechnicalPurge</c>; <c>TechnicalPurgeJob</c> runs them all
/// every night, one at a time, and one failing does not stop the others. A purge runs OUTSIDE any
/// tenant — Hangfire has none — so it reads with <c>IgnoreQueryFilters()</c> and applies to every
/// tenant at once: the durations are the platform's, not a tenant's (owner, 2026-10-24).
/// </para>
/// </summary>
public interface ITechnicalPurge
{
    /// <summary>What the log line says was purged, e.g. « leads.outbox_messages ».</summary>
    string Name { get; }

    /// <returns>How many rows were deleted or emptied.</returns>
    Task<int> PurgeAsync(DateTimeOffset now, CancellationToken ct);
}

/// <summary>
/// How long technical data is kept — fixed by the platform (owner, 2026-10-24), in ONE place so a
/// reviewer reads every duration at once. The audit journal is not here: it is never purged.
/// </summary>
public static class TechnicalRetention
{
    /// <summary>An event already published, an inbound message already deduplicated.</summary>
    public static readonly TimeSpan ProcessedMessages = TimeSpan.FromDays(30);

    /// <summary>A refresh token expired or revoked: it can no longer open anything.</summary>
    public static readonly TimeSpan DeadTokens = TimeSpan.FromDays(30);

    /// <summary>A session ended (or idle that long): the « Mes sessions » screen has no use for it.</summary>
    public static readonly TimeSpan EndedSessions = TimeSpan.FromDays(90);

    /// <summary>Where and how a user signed in — kept a year for a security investigation.</summary>
    public static readonly TimeSpan LoginLocations = TimeSpan.FromDays(365);

    /// <summary>A notification read by its user.</summary>
    public static readonly TimeSpan ReadNotifications = TimeSpan.FromDays(90);

    /// <summary>Any notification, read or not.</summary>
    public static readonly TimeSpan AnyNotification = TimeSpan.FromDays(365);

    /// <summary>An e-mail or SMS sent (or given up on), and what the provider said about it.</summary>
    public static readonly TimeSpan MessageDelivery = TimeSpan.FromDays(180);

    /// <summary>
    /// The file an import read and the detail of its rejected rows — both copies of personal data.
    /// The job's row (who, when, how many) stays.
    /// </summary>
    public static readonly TimeSpan ImportFiles = TimeSpan.FromDays(30);

    /// <summary>What a source sent for a lead, as received — the lead itself holds the data now.</summary>
    public static readonly TimeSpan RawIngestions = TimeSpan.FromDays(90);
}
