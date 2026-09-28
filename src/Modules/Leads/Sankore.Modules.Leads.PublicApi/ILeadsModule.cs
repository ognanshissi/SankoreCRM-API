namespace Sankore.Modules.Leads.PublicApi;

/// <summary>
/// Contract other modules may use to read minimal lead information.
/// Kept intentionally small today (MVP scope); grows only when a concrete
/// cross-module need appears (e.g. Customers module querying original lead
/// source after conversion for attribution reporting).
/// </summary>
public interface ILeadsModule
{
    Task<LeadSummary?> GetLeadAsync(Guid leadId, CancellationToken ct);

    /// <summary>
    /// Commercial history of a lead — activities, field visits, follow-up reminders and
    /// notes — projected as timeline-ready items (F13.29 / US-M01-BE-26).
    /// Consumed by module M01 when a converted lead's history is imported into the
    /// client timeline, so the relationship does not appear to start at conversion.
    ///
    /// NO SENSITIVE DATA ever travels on this contract: the projection carries only
    /// structured labels (activity type, outcome, reminder title, subject), never a
    /// phone number, an e-mail, an identity document number, a date of birth, free-text
    /// notes bodies, GPS coordinates or a photo reference. A caller that needs any of
    /// those must go through the owning module's audited endpoint.
    ///
    /// Returns an empty list when the lead does not exist in that tenant — the caller is
    /// an at-least-once event consumer and must not fail on a lead that was purged.
    /// </summary>
    Task<IReadOnlyList<LeadHistoryItem>> GetLeadHistoryAsync(Guid tenantId, Guid leadId, CancellationToken ct);
}

public sealed record LeadSummary(
    Guid Id,
    string FullName,
    string Status,
    string Source,
    Guid? AssignedAgentId);

/// <summary>
/// One non-sensitive entry of a lead's commercial history.
/// <paramref name="EntryType"/> is an UPPER_SNAKE label (e.g. <c>LEAD_ACTIVITY_CALL</c>),
/// <paramref name="ReferenceType"/>/<paramref name="ReferenceId"/> form an opaque back-reference
/// into the Leads module (never a physical foreign key).
/// </summary>
public sealed record LeadHistoryItem(
    string EntryType,
    DateTimeOffset OccurredAt,
    string Summary,
    string? ReferenceType,
    string? ReferenceId);
