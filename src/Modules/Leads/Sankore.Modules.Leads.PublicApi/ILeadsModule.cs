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
    /// The names of several leads in one read (connected mailbox: a page of conversations, or a
    /// search over them). Ambient tenant, like <see cref="GetLeadAsync"/>; an unknown id is simply
    /// absent from the answer.
    /// </summary>
    Task<IReadOnlyDictionary<Guid, string>> GetLeadNamesAsync(IReadOnlyCollection<Guid> leadIds, CancellationToken ct);

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
    /// Which leads carry these e-mail addresses (connected mailbox, lot 2) — so a synced e-mail is
    /// kept only when it was exchanged with somebody the CRM knows.
    ///
    /// <para>
    /// A BATCH, because one page of a mailbox names dozens of addresses and a call per address
    /// would be a query per address. The index is computed here under M13's key, for the reason
    /// <see cref="FindOpenLeadsByPhoneAsync"/> gives. EVERY non-anonymised lead, whatever its
    /// status: an exchange with a lost or converted lead is still part of its history. What comes
    /// back is the address as the caller gave it (lower-cased) and an id — nothing the caller did
    /// not already hold.
    /// </para>
    /// </summary>
    Task<IReadOnlyList<LeadEmailMatch>> MatchLeadsByEmailsAsync(
        Guid tenantId, IReadOnlyCollection<string> emails, CancellationToken ct);

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

    /// <summary>
    /// The files this module holds about a client, for the client record's « Documents » tab:
    /// attachments of their projects and of the tasks about them, WhatsApp media of their leads'
    /// conversations. Metadata only; explicit tenant.
    /// </summary>
    Task<IReadOnlyList<CustomerDocumentEntry>> ListCustomerDocumentsAsync(
        Guid tenantId, Guid customerId, CancellationToken ct);

    /// <summary>
    /// Records what a WhatsApp number received — customers' messages and the delivery statuses of
    /// ours (WA-BE-07 → WA-BE-10). Called by the integration module's webhook job, under the SYSTEM
    /// identity, once the delivery has been verified at the door.
    ///
    /// <para>
    /// <b>The content arrives in CLEAR and is encrypted here</b>, with this module's key: the
    /// conversation is M13's record, and the integration module keeps only the encrypted raw body
    /// it received. Nothing in this batch reaches a log.
    /// </para>
    ///
    /// <para>
    /// <b>Idempotent on the provider's message id</b>, so a webhook replayed by its job — or
    /// redelivered by the provider in another batch — records each message once and applies each
    /// status at most once in the forward direction.
    /// </para>
    /// </summary>
    /// <param name="connectionAgencyId">
    /// The agency the receiving number serves, when it serves one. A lead created from an unknown
    /// number is filed there.
    /// </param>
    Task<MessagingEventsOutcome> RecordMessagingEventsAsync(
        Guid tenantId,
        Guid connectionId,
        Guid? connectionAgencyId,
        MessagingEventBatch batch,
        CancellationToken ct);

    /// <summary>
    /// One rendez-vous, cancelled or not, for the copy into connected agendas; null when it does
    /// not exist in this tenant.
    /// </summary>
    Task<AppointmentCalendarEntry?> GetAppointmentForCalendarAsync(Guid tenantId, Guid appointmentId, CancellationToken ct);

    /// <summary>
    /// The SCHEDULED rendez-vous a user organises or is invited to, overlapping
    /// [<paramref name="from"/>, <paramref name="to"/>) — what a newly chosen target agenda receives.
    /// </summary>
    Task<IReadOnlyList<AppointmentCalendarEntry>> ListAppointmentsForUserAsync(
        Guid tenantId, Guid userId, DateTimeOffset from, DateTimeOffset to, CancellationToken ct);

    /// <summary>
    /// The lead as an erasure request sees it: is it there, already erased, and is its person a
    /// client — linked or converted. A client's erasure follows the client's legal retention, not
    /// the lead's (data-purge lot 2). Null: no such lead in that tenant.
    /// </summary>
    Task<LeadErasureSubject?> GetErasureSubjectAsync(Guid tenantId, Guid leadId, CancellationToken ct);

    /// <summary>
    /// Erases one lead now — the same cascade as the retention job (<c>LeadErasure</c>) — and
    /// publishes <c>LeadErasedEvent</c> so the modules holding copies (calls, recordings) follow.
    /// Only for a request a second person approved: the caller checked eligibility. False when
    /// there was nothing to do (unknown, or already erased).
    /// </summary>
    Task<bool> EraseLeadAsync(Guid tenantId, Guid leadId, CancellationToken ct);

    /// <summary>
    /// The answers of a project's questionnaires as an insurance subscription carries them: every
    /// expected questionnaire, per person, in a stable order, with a SHA-256 of that text. The
    /// subscription keeps the copy the client signed; the fingerprint is compared again when it
    /// leaves for the insurer. Null: no such project, or its product asks no questionnaire.
    /// </summary>
    Task<ProjectAnswersSnapshot?> GetProjectAnswersSnapshotAsync(
        Guid tenantId, Guid opportunityId, CancellationToken ct);

    /// <summary>
    /// Marks the project's answers as sent to the insurer: they can no longer be edited, and their
    /// retention period starts (BNF-B-09). Idempotent.
    /// </summary>
    Task MarkProjectAnswersTransmittedAsync(Guid tenantId, Guid opportunityId, CancellationToken ct);
}

/// <param name="IsComplete">Every expected questionnaire is answered.</param>
/// <param name="AnswersJson">
/// The answers, per person and questionnaire. Health data when <paramref name="ContainsHealthData"/>
/// is true — the caller stores it encrypted.
/// </param>
public sealed record ProjectAnswersSnapshot(
    bool IsComplete,
    int MissingCount,
    bool ContainsHealthData,
    string AnswersJson,
    string Sha256);

/// <summary>
/// One request a customer has in progress (BNF-B-22). Non-sensitive by construction.
/// </summary>
/// <param name="ProductLineId">
/// The M12 product line of the request's open PROJECT (the lead stores none), or null while no
/// project names a catalogue product. The client record uses it to warn that a request duplicates a
/// product line the customer already holds.
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
/// <param name="Email">The address as asked, trimmed and lower-cased.</param>
public sealed record LeadEmailMatch(string Email, Guid LeadId);

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

/// <summary>What one webhook delivery carried, as M13 records it.</summary>
public sealed record MessagingEventBatch(
    IReadOnlyList<InboundMessagingMessage> Messages,
    IReadOnlyList<MessagingDeliveryStatus> Statuses);

/// <summary>
/// One message a customer sent to the institution's number.
/// </summary>
/// <param name="FromE164">The sender, with the plus. Personal data.</param>
/// <param name="ProfileName">What the sender's WhatsApp profile calls them. Not an identity.</param>
/// <param name="Type">
/// <c>text</c>, <c>image</c>, <c>document</c>, <c>audio</c>, <c>video</c>, <c>sticker</c>,
/// <c>location</c>, <c>contacts</c>, <c>button</c>, <c>interactive</c>, <c>reaction</c> or
/// <c>unsupported</c>.
/// </param>
/// <param name="Text">The text, the caption, or the label of the button pressed.</param>
public sealed record InboundMessagingMessage(
    string ProviderMessageId,
    string FromE164,
    string? ProfileName,
    DateTimeOffset SentAt,
    string Type,
    string? Text,
    string? MediaId,
    string? MediaMimeType,
    string? MediaFileName,
    string? ReplyToProviderMessageId)
{
    /// <summary>Identifiers only: the generated form would print the number and the text.</summary>
    public override string ToString() => $"InboundMessagingMessage {{ {ProviderMessageId}, {Type} }}";
}

/// <param name="Status"><c>sent</c>, <c>delivered</c>, <c>read</c> or <c>failed</c>.</param>
/// <param name="ErrorCode">
/// One of the integration module's <c>MESSAGING_*</c> codes, already translated — M13 never reads
/// a provider's error number.
/// </param>
/// <param name="ErrorDetail">The provider's sentence about the failure, in English. Never personal data.</param>
public sealed record MessagingDeliveryStatus(
    string ProviderMessageId,
    string Status,
    DateTimeOffset At,
    string? ErrorCode,
    string? ErrorDetail);

/// <param name="StatusesUnmatched">
/// Statuses naming a message M13 does not know. Ordinary for a status that overtook the save of
/// the message it describes — the caller retries a recent delivery for exactly that case.
/// </param>
public sealed record MessagingEventsOutcome(
    int MessagesRecorded,
    int MessagesDuplicate,
    int StatusesApplied,
    int StatusesUnmatched);

/// <param name="Reference">What identifies the lead once its name is gone — never personal data.</param>
/// <param name="ClientId">The client the lead's person is (linked or converted); null for a prospect.</param>
public sealed record LeadErasureSubject(Guid LeadId, string Reference, bool IsErased, Guid? ClientId);
