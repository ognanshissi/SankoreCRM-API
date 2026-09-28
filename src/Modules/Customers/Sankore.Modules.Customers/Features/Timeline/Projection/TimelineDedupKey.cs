namespace Sankore.Modules.Customers.Features.Timeline.Projection;

/// <summary>
/// Builds the <c>DedupKey</c> that backs the <c>ux_client_timeline_dedup</c> unique index.
///
/// The key must identify the FACT, not the delivery: it has to stay identical across a
/// broker redelivery, an outbox republish and a manual replay, otherwise the unique index
/// stops protecting anything. Hence <see cref="ForEvent"/> keys on the integration event's
/// <c>EventId</c> (stable, assigned once by the producer) and never on the transport
/// message id.
/// </summary>
internal static class TimelineDedupKey
{
    /// <summary>Preferred form: the fact already carries a stable identity.</summary>
    internal static string ForEvent(string sourceModule, string entryType, Guid eventId)
        => $"{sourceModule}:{entryType}:{eventId:D}";

    /// <summary>
    /// Fallback for facts imported from a store rather than received as an event
    /// (e.g. the lead history pulled through <c>ILeadsModule</c>): the natural key is
    /// the referenced row plus its instant.
    /// </summary>
    internal static string ForFact(
        string sourceModule, string entryType, string? referenceId, Guid clientId, DateTimeOffset occurredAt)
        => $"{sourceModule}:{entryType}:{referenceId ?? clientId.ToString("D")}:{occurredAt:O}";
}
