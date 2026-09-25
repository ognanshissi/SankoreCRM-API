namespace Sankore.Modules.Leads.Domain;

/// <summary>
/// Optimistic concurrency for client-driven lead edits, based on <see cref="Lead.UpdatedAt"/>
/// — every mutator bumps it. The client echoes the <c>updatedAt</c> it last read and the
/// handler refuses the write when the row has moved on since.
/// </summary>
/// <remarks>
/// Compared at millisecond granularity on purpose: Postgres stores microseconds while a
/// JavaScript <c>Date</c> truncates to milliseconds, so an exact comparison would reject
/// every browser client that parses the timestamp instead of echoing the raw string.
/// This guard is opt-in per endpoint and does not protect against background jobs writing
/// concurrently — that would require a database-level token (xmin) on the whole table.
/// </remarks>
public static class LeadConcurrency
{
    /// <summary>Error code returned by handlers when the expected version does not match.</summary>
    public const string ConflictError = "CONFLICT";

    /// <summary>
    /// True when the caller's expected version is stale. A null expectation means the
    /// client opted out of the check (older clients), and is never treated as a conflict.
    /// </summary>
    public static bool IsStale(this Lead lead, DateTimeOffset? expectedUpdatedAt)
        => expectedUpdatedAt is { } expected
           && expected.ToUnixTimeMilliseconds() != lead.UpdatedAt.ToUnixTimeMilliseconds();
}
