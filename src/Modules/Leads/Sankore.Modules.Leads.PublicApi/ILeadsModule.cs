namespace Sankore.Modules.Leads.PublicApi;

using Sankore.Shared.Kernel;

/// <summary>
/// Contract other modules may use to read minimal lead information.
/// Kept intentionally small today (MVP scope); grows only when a concrete
/// cross-module need appears (e.g. Customers module querying original lead
/// source after conversion for attribution reporting).
///
/// <para>
/// <b>One member returns a sensitive value and it is named here so nobody has to discover
/// it:</b> <see cref="ResolvePhoneForCallAsync"/>, which exists so a telephone number never has
/// to travel through a browser to be dialled. Everything else on this contract carries
/// identifiers and structured labels only — see <see cref="GetLeadHistoryAsync"/>, whose
/// projection rules still stand unchanged.
/// </para>
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
    /// Creates a lead from a call nobody answered (TEL-B-13).
    ///
    /// <para>
    /// <b>The name is SYNTHETIC and that is assumed, not worked around.</b> <c>Lead.Capture</c>
    /// requires a name and an unanswered call from an unknown number has none — nobody spoke. So
    /// the lead is created with a placeholder the agent replaces on their first real contact, and
    /// the placeholder is built HERE rather than by the caller: a label an agent reads is this
    /// module's vocabulary, and a telephony module composing French prospect names would be a
    /// second place to translate.
    /// </para>
    ///
    /// <para>
    /// <b>Idempotent on the NUMBER, not on the call.</b> Two calls from one number are one person
    /// trying twice, so this answers the existing open lead rather than creating a second — which
    /// is also why it cannot be built on <c>SystemCaptureLead</c>, whose <c>Force: true</c> turns
    /// the duplicate gate off entirely. A number that already belongs to an open lead returns that
    /// lead's id with <c>Created: false</c>, and the caller attaches the call to it.
    /// </para>
    ///
    /// <para>
    /// The number arrives in CLEAR, like <c>FindOpenLeadsByPhoneAsync</c>'s and for the same
    /// reason: this module holds the blind-index key and no caller can compute an index it would
    /// match.
    /// </para>
    /// </summary>
    /// <param name="ownerId">
    /// The agent the call was for, who becomes the lead's owner. A lead created from a missed call
    /// and routed to nobody would sit in a queue with no SLA and no owner, which is the one
    /// outcome worse than not creating it.
    /// </param>
    Task<Result<MissedCallLeadResult>> CaptureFromMissedCallAsync(
        Guid tenantId,
        string phoneNumber,
        Guid ownerId,
        Guid? agencyId,
        DateTimeOffset missedAt,
        CancellationToken ct);

    /// <summary>
    /// Whether <paramref name="actorUserId"/> may work this lead (TEL-B-09).
    ///
    /// <para>
    /// The same question <see cref="ResolvePhoneForCallAsync"/> answers on its way to a number,
    /// asked on its own — for a caller that needs the verdict without the number. One definition
    /// behind both: the lead's current assignee, its owner, the agent who collected it, or their
    /// supervisor. A permission cannot express it, because every commercial agent holds the same
    /// one.
    /// </para>
    ///
    /// <para>
    /// <paramref name="actorUserId"/> must be the AUTHENTICATED caller of the request this runs
    /// in, and is refused when it is not — see <see cref="ResolvePhoneForCallAsync"/> for why.
    /// False for a lead that does not exist, which a caller holding an arbitrary identifier
    /// cannot tell from a refusal.
    /// </para>
    /// </summary>
    Task<bool> CanActorWorkLeadAsync(
        Guid tenantId, Guid leadId, Guid actorUserId, CancellationToken ct);

    /// <summary>
    /// A lead's telephone number in clear, <b>for dialling and for nothing else</b> (TEL-B-06).
    ///
    /// <para>
    /// The exception to this contract's rule, and the reason it is an exception: the alternative
    /// was for the browser to send a number, and a contract taking a number is a contract a
    /// browser can be made to post somebody else's number to. So the browser names a RECORD and a
    /// SLOT, the number is read here, and the one caller — the click-to-call slice — hands it
    /// straight to a telephony provider's API. It reaches no response, no log, no event and no
    /// column.
    /// </para>
    ///
    /// <para>
    /// <b>Who may dial is the same question as who may work the lead</b>, which this module
    /// already answers for a household write: the current assignee, the owner, the agent who
    /// collected it, or their supervisor. A permission alone cannot express it — every commercial
    /// agent holds the same one, and they must not ring each other's prospects.
    /// </para>
    ///
    /// <para>
    /// <paramref name="actorUserId"/> must be the AUTHENTICATED caller of the request this runs
    /// in. It is refused when it is not: the ownership rule is evaluated against the ambient
    /// identity, so a background caller passing somebody else's id would be asking this module to
    /// vouch for a perimeter check it did not make.
    /// </para>
    /// </summary>
    /// <param name="slot">
    /// <c>Primary</c> is the prospect's own number; <c>Secondary</c> is the company's, for a
    /// corporate lead. A slot that holds nothing answers a failure rather than an empty string.
    /// </param>
    Task<Result<string>> ResolvePhoneForCallAsync(
        Guid tenantId, Guid leadId, PhoneSlot slot, Guid actorUserId, CancellationToken ct);

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

/// <param name="Created">
/// False when the number already belonged to an open lead, which is the ordinary answer for a
/// caller who tried twice. The caller attaches its call either way.
/// </param>
public sealed record MissedCallLeadResult(Guid LeadId, bool Created);

/// <summary>
/// Which of a lead's numbers to dial (TEL-B-06). Its own enum rather than one shared through the
/// Kernel with M01's: the two meanings differ — M01's slots are contact points, these are the
/// prospect's own number and their company's — and a single enum would invite a caller to assume
/// they map onto each other.
/// </summary>
public enum PhoneSlot
{
    Primary,
    Secondary,
}

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
