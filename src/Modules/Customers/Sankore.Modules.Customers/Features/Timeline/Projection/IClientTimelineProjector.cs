namespace Sankore.Modules.Customers.Features.Timeline.Projection;

/// <summary>
/// The single write path into <c>customers.client_timeline_entries</c> (US-M01-BE-26).
///
/// Every producer — the module's own event consumers today, M02/M03/M04/M08 consumers
/// tomorrow — goes through this interface, so the idempotency rule and the
/// no-sensitive-data rule are enforced in exactly one place.
/// </summary>
public interface IClientTimelineProjector
{
    /// <summary>
    /// Appends one timeline entry, or does nothing when an entry with the same
    /// <paramref name="dedupKey"/> already exists in the tenant.
    ///
    /// <paramref name="summary"/> must never contain a sensitive value (identity document
    /// number, phone, e-mail, date of birth). The implementation redacts what it can
    /// detect, but callers are responsible for building summaries out of structured,
    /// non-sensitive data.
    /// </summary>
    /// <param name="dedupKey">
    /// Stable identity of the projected fact. Use the integration event's
    /// <c>EventId</c> whenever one exists; otherwise
    /// <c>$"{sourceModule}:{entryType}:{referenceId ?? clientId}:{occurredAt:O}"</c>.
    /// </param>
    Task AppendAsync(
        Guid tenantId,
        Guid clientId,
        string sourceModule,
        string entryType,
        DateTimeOffset occurredAt,
        string summary,
        string? referenceType,
        string? referenceId,
        string dedupKey,
        CancellationToken ct);
}
