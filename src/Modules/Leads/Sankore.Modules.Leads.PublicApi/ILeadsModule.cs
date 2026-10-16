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

    /// <summary>
    /// The OPEN leads whose telephone number is this one (TEL-B-09).
    ///
    /// <para>
    /// <b>The number arrives in CLEAR, and that is not an oversight.</b> Each module holds its own
    /// blind-index key, so no caller can compute an index this module would match — asking it to
    /// is the only way a reverse lookup can work at all, and it is the same arrangement
    /// <c>ICustomersModule.FindMatchesAsync</c> already makes. What comes back is identifiers and
    /// a display name, never the number, so the clear value travels one way only.
    /// </para>
    ///
    /// <para>
    /// OPEN leads only. A converted lead belongs to a client and is found through M01; a lost,
    /// disqualified or archived one is a conversation that ended, and attaching today's call to it
    /// would file the exchange against a record nobody is working.
    /// </para>
    ///
    /// <para>
    /// Several matches is an ORDINARY answer, not an ambiguity to resolve here: a family handset
    /// or a shop line is common, and the caller — who alone knows the agent's perimeter — decides.
    /// Empty when nothing matches, never null.
    /// </para>
    /// </summary>
    Task<IReadOnlyList<LeadPhoneMatch>> FindOpenLeadsByPhoneAsync(
        Guid tenantId, string phoneNumber, CancellationToken ct);

    /// <summary>
    /// The requests a known customer currently has in progress (BNF-B-22) — what the client record
    /// shows so an advisor opening it can see "this client is already asking for a loan" instead of
    /// starting a second one.
    ///
    /// <para>
    /// OPEN only: converted, lost, disqualified and archived leads are excluded, because the
    /// question the client record asks is "what is live right now". A caller wanting the history of
    /// closed requests is asking something else and should say so.
    /// </para>
    ///
    /// <para>
    /// NO SENSITIVE DATA, the same rule as <see cref="GetLeadHistoryAsync"/>: a product label, a
    /// status, a stage and dates. Never the prospect's own contact details — the client record
    /// already holds the authoritative copy of those, and a second one travelling on this contract
    /// would be the copy that goes stale.
    /// </para>
    ///
    /// <para>
    /// Returns an empty list for a customer with no open request, and for one M13 has never heard
    /// of. The caller is a screen, and a missing customer is not an error it can act on.
    /// </para>
    /// </summary>
    Task<IReadOnlyList<CustomerOpenLead>> GetOpenLeadsForCustomerAsync(
        Guid tenantId, Guid customerId, CancellationToken ct);
}

/// <summary>
/// One request a customer has in progress (BNF-B-22). Non-sensitive by construction.
/// </summary>
/// <param name="ProductLineId">
/// The resolved M12 product line, or null when nobody has resolved it yet. The client record uses
/// it to warn that a request duplicates a product line the customer already holds.
/// </param>
/// <param name="InterestedProduct">The product as CAPTURED — a free-text label, not a catalogue code.</param>
public sealed record CustomerOpenLead(
    Guid LeadId,
    string Status,
    string PipelineStage,
    Guid? ProductLineId,
    string InterestedProduct,
    Guid? AssignedAgentId,
    DateTimeOffset CapturedAt,
    DateTimeOffset? LastActivityAt);

/// <param name="AgencyId">
/// The lead's agency, so the caller can apply its own perimeter. Null when the lead carries none
/// — which M13 documents as ordinary, and which the caller must treat as "outside every
/// restricted perimeter" rather than as "inside all of them".
/// </param>
public sealed record LeadPhoneMatch(
    Guid LeadId,
    string FullName,
    string Status,
    Guid? AgencyId,
    Guid? AssignedAgentId);

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
