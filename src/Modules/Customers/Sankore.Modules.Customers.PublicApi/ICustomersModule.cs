namespace Sankore.Modules.Customers.PublicApi;

using Sankore.Shared.Kernel;

/// <summary>
/// Public contract exposed by module M01 (Customers).
/// Other modules reference ONLY this project — never the main assembly.
///
/// Every method takes an explicit <paramref name="tenantId"/> because callers may
/// run outside an HTTP request (Hangfire jobs, MassTransit consumers) where the
/// ambient <see cref="ITenantContext"/> is not the tenant being operated on. The
/// implementation therefore bypasses the global query filters and re-applies the
/// tenant predicate manually.
///
/// No method on this contract ever returns decrypted sensitive data: a caller that
/// needs a client's phone number, e-mail, document number, date of birth or income
/// must go through the audited reveal endpoint of module M01.
/// </summary>
public interface ICustomersModule
{
    /// <summary>
    /// Lightweight, non-sensitive projection of a client, or <c>null</c> when the
    /// client does not exist in that tenant.
    /// </summary>
    Task<ClientSummary?> GetClientSummaryAsync(Guid tenantId, Guid clientId, CancellationToken ct);

    /// <summary>
    /// The same projection as <see cref="GetClientSummaryAsync"/> for a batch of ids, keyed by the
    /// id that was ASKED for — so a caller holding a page of opaque references can line the answers
    /// up positionally without a second lookup.
    ///
    /// <para>
    /// Ids that do not exist in that tenant are simply absent from the dictionary: a list screen
    /// resolving names must degrade to "unknown" on a dangling reference, not fail the page.
    /// Merge chains are NOT followed, exactly as in the single read — the answer describes the
    /// record pointed at, and a caller that wants the survivor resolves through
    /// <see cref="ResolveClientIdAsync"/> first.
    /// </para>
    ///
    /// <para>
    /// It exists so a cross-module list costs ONE query per page instead of one per row. Callers
    /// must therefore keep the batch bounded to a page's worth of ids: the implementation turns
    /// them into a single SQL <c>IN</c>, which stops being a favour somewhere in the thousands.
    /// </para>
    /// </summary>
    Task<IReadOnlyDictionary<Guid, ClientSummary>> GetClientSummariesAsync(
        Guid tenantId, IReadOnlyCollection<Guid> clientIds, CancellationToken ct);

    /// <summary>
    /// Follows the merge chain and returns the id of the surviving client record
    /// (the same id when the client was never merged), or <c>null</c> when the
    /// client does not exist. Callers holding a possibly stale client id should
    /// always resolve it through this method before writing a reference.
    /// </summary>
    Task<Guid?> ResolveClientIdAsync(Guid tenantId, Guid clientId, CancellationToken ct);

    /// <summary>
    /// True only when the client exists AND its status is Active — i.e. KYC has been
    /// approved and the record is neither pending, suspended, rejected, archived nor merged.
    /// </summary>
    Task<bool> ExistsAndActiveAsync(Guid tenantId, Guid clientId, CancellationToken ct);

    /// <summary>
    /// Creates (or returns the already-created) client for a converted lead.
    /// Idempotent on <see cref="CreateFromLeadRequest.LeadId"/>: calling it twice for
    /// the same lead returns the same client with <c>AlreadyExisted = true</c>.
    /// </summary>
    Task<Result<CreateFromLeadResult>> CreateFromLeadAsync(CreateFromLeadRequest request, CancellationToken ct);
    
    /// <summary>
    /// Checks whether a customer with the given ID exists for the tenant.
    /// Used by Leads module for convert-to-existing-customer validation (US-M13-171).
    /// </summary>
    Task<bool> ExistsAsync(Guid tenantId, Guid customerId, CancellationToken ct);

    /// <summary>
    /// Whether the origin of the client's patrimony and funds has been RECORDED — a boolean, never
    /// the value.
    ///
    /// <para>
    /// The CIMA LBC/FT text requires an institution to « établir l'origine du patrimoine et des
    /// fonds » before entering into a relationship with a politically exposed person. M02 is where
    /// that relationship is authorised, so M02 has to be able to ask — but the narrative behind
    /// the answer describes a named person's assets, and the one thing M02 needs is whether the
    /// work was done.
    /// </para>
    ///
    /// <para>
    /// <c>false</c> for an unknown client, deliberately: a gate that read "client not found" as
    /// "requirement satisfied" would be satisfied by every mistake.
    /// </para>
    /// </summary>
    Task<bool> HasRecordedFundsOriginAsync(Guid tenantId, Guid clientId, CancellationToken ct);

    /// <summary>
    /// Whether the client has an active email address on file — a boolean, never the address
    /// (KRM-B-03).
    ///
    /// <para>
    /// M02 needs it to decide, BEFORE issuing anything, whether a remote-collection link can reach
    /// its customer, and to tell the agent plainly why no link was sent when it cannot. The
    /// address itself stays here: it is AES-256-GCM in <c>client_contact_points</c> under M01's
    /// key, and handing it across the boundary would create a second, unprotected home for it.
    /// </para>
    ///
    /// <para>
    /// <c>false</c> for an unknown client, and <c>false</c> for an address this module cannot
    /// decrypt. Both are "do not promise a customer a link they will never get".
    /// </para>
    /// </summary>
    Task<bool> HasEmailAsync(Guid tenantId, Guid clientId, CancellationToken ct);

    /// <summary>
    /// The client's active email address MASKED (<c>j***@e***.com</c>), or null when there is none
    /// (KRM-F-07).
    ///
    /// <para>
    /// For a confirmation dialog: an agent about to send a single-use collection link needs to
    /// recognise where it is going, and the mask is enough to tell "the right customer" from "the
    /// address we have on file is wrong". The same projection M01's own contact-point list already
    /// shows, so this exposes nothing a screen in this product does not show already — the clear
    /// value stays behind the audited reveal endpoint.
    /// </para>
    ///
    /// <para>
    /// Separate from <see cref="HasEmailAsync"/> rather than replacing it, because the two have
    /// different costs and different consequences. This one DECRYPTS; that one does not. A gate
    /// deciding whether a link can be issued must not depend on the field-encryption key being
    /// right — an address that will not decrypt is an incident to fix, not a customer without an
    /// email — so the gate asks the cheap question and only a display asks this one.
    /// </para>
    /// </summary>
    Task<string?> GetMaskedEmailAsync(Guid tenantId, Guid clientId, CancellationToken ct);

    /// <summary>
    /// Whether the client number and date of birth a CUSTOMER typed on the public remote-KYC page
    /// match the client the KYC file belongs to (KRM-B-05).
    ///
    /// <para>
    /// <b>A boolean, and the question is asked rather than answered.</b> M02 holds the file and the
    /// link; M01 holds the client number and the date of birth — the second of which is AES-256-GCM
    /// in <c>clients.encrypted_date_of_birth</c> with an HMAC blind index beside it. Handing either
    /// value across the boundary so M02 could compare them itself would copy a regulated field into
    /// a module that has no key for it, and the comparison would then exist in two places.
    /// </para>
    ///
    /// <para>
    /// <b>No value is ever returned, and none is logged.</b> The caller is an anonymous endpoint:
    /// the only thing it may learn is yes or no, because anything else is an oracle for guessing a
    /// date of birth. The attempt itself is audited by M02 on the invitation — without the values
    /// that were typed.
    /// </para>
    ///
    /// <para>
    /// <c>false</c> for an unknown client, for a client with no recorded date of birth, and for a
    /// client of another tenant. The date of birth is nullable on a converted lead that never
    /// carried one, and such a client cannot identify remotely at all — which is correct: there is
    /// no second factor to check, and the agent collects at the counter.
    /// </para>
    /// </summary>
    /// <param name="clientNumber">
    /// As the customer typed it, from their welcome email. Normalised by this module — spacing and
    /// casing — so the caller does not have to know the format.
    /// </param>
    Task<bool> VerifyRemoteIdentityAsync(
        Guid tenantId,
        Guid clientId,
        string? clientNumber,
        DateOnly? birthDate,
        CancellationToken ct);

    /// <summary>
    /// Whether the surname, given names and date of birth READ on a customer's identity document
    /// agree with the client record (KRM-B-08, section 3.2).
    ///
    /// <para>
    /// <b>The caller holds the reading; this module holds the record.</b> The two names are clear
    /// columns here and the date of birth is AES-256-GCM with an HMAC index beside it, so M02 sends
    /// what it read and gets a boolean — the record never crosses the boundary, and the
    /// normalisation rule ("sans accents ni casse") stays in the one class that already defines it
    /// for client search.
    /// </para>
    ///
    /// <para>
    /// <b><c>false</c> whenever the question cannot be answered</b>: an unknown client, a client of
    /// another tenant, a client with no recorded date of birth, and — importantly — an INCOMPLETE
    /// reading. A document whose surname the extractor could not produce is not a document that
    /// agrees with the record; it is a document nobody checked, and the condition it feeds is a
    /// positive one. The file then goes to a human, which is the only safe direction for a gate
    /// that can otherwise approve a relationship unattended.
    /// </para>
    ///
    /// <para>
    /// The comparison is asymmetric on the given names and strict on the surname: every given name
    /// on the RECORD must appear among those read, not the reverse, because a card lists a
    /// person's full civil names and a counter capture routinely holds fewer. The surname is one
    /// field on both sides and must match outright.
    /// </para>
    ///
    /// <para>
    /// No value is returned and none is logged — only which of the three comparisons disagreed.
    /// </para>
    /// </summary>
    /// <param name="readSurname">The surname as the OCR produced it, unnormalised.</param>
    /// <param name="readGivenNames">
    /// The given names as the OCR produced them, in one string — which is how both the MRZ and the
    /// service's <c>given_names</c> field carry them.
    /// </param>
    Task<bool> MatchesReadIdentityAsync(
        Guid tenantId,
        Guid clientId,
        string? readSurname,
        string? readGivenNames,
        DateOnly? readBirthDate,
        CancellationToken ct);

    /// <summary>
    /// Sends a templated notification to one of the tenant's CLIENTS, and answers whether it was
    /// queued (KRM-B-03).
    ///
    /// <para>
    /// <b>A send, not an address lookup</b> — the exact arrangement
    /// <c>IAdministrationModule.NotifyUserAsync</c> uses for users, and for the same reasons. This
    /// module owns the encrypted address, the client's preferred language and the locale fallback
    /// to the tenant default; a caller handed an address would duplicate that fallback, and a
    /// wrong locale does not degrade gracefully — PostgreSQL matches template locales
    /// case-sensitively and a miss mails the message's own JSON payload as its body.
    /// </para>
    ///
    /// <para>
    /// It also fills the recipient's own identity into the template — <c>full_name</c>,
    /// <c>client_first_name</c> and <c>company_name</c> — so no caller has to carry a customer's
    /// name through a Hangfire payload or an event to send them a message.
    /// </para>
    ///
    /// <para>
    /// <b>Never throws.</b> A client with no address, an unknown client, an undecryptable address
    /// and a failure to queue all return <c>false</c>, because every caller is a consumer or a job
    /// whose own work must stand whether or not the mail went out.
    /// </para>
    /// </summary>
    Task<bool> NotifyClientAsync(NotifyClientRequest request, CancellationToken ct);

    /// <summary>
    /// Whether the client has an active telephone number on file — a boolean, never the number.
    ///
    /// <para>
    /// The twin of <see cref="HasEmailAsync"/> and, like it, it does NOT decrypt: a gate deciding
    /// whether a second factor can reach somebody must not depend on the field-encryption key
    /// being right. A number that will not decrypt surfaces at send time, which is the right place
    /// for a key problem to appear.
    /// </para>
    /// </summary>
    Task<bool> HasPhoneAsync(Guid tenantId, Guid clientId, CancellationToken ct);

    /// <summary>
    /// Sends a templated TEXT MESSAGE to one of the tenant's clients, and answers whether it was
    /// queued.
    ///
    /// <para>
    /// A send and not a number lookup, for the reasons <see cref="NotifyClientAsync"/> gives —
    /// plus one specific to this channel: a stored telephone number is whatever was dialled, and
    /// turning <c>0708091801</c> into the <c>+2250708091801</c> a provider needs takes the
    /// tenant's configured calling code. A caller handed the raw number would have to know that,
    /// and would get it wrong for the one tenant whose customers were captured abroad.
    /// </para>
    ///
    /// <para>
    /// <b>Never throws</b>, and <c>false</c> covers every ordinary reason nothing was sent: an
    /// unknown client, no telephone on file, a number that cannot be made dialable, and a tenant
    /// with no SMS channel — which is most of them. Every caller is a consumer, a job or an
    /// anonymous endpoint whose own work must stand regardless.
    /// </para>
    ///
    /// <para>
    /// <b><c>true</c> means queued, not delivered.</b> On this channel the gap is wider than on
    /// email: whether the tenant's allowance is exhausted and whether a template exists are
    /// settled later by the outbox processor, which dead-letters with a reason. A caller that must
    /// not strand somebody — a second-factor flow — asks
    /// <c>IAdministrationModule.GetSmsConfigAsync</c> first and reads <c>CanSend</c>.
    /// </para>
    /// </summary>
    Task<bool> NotifyClientBySmsAsync(NotifyClientBySmsRequest request, CancellationToken ct);

    /// <summary>
    /// Clients holding a live product whose CONTRACT ANNIVERSARY falls on
    /// <paramref name="onDay"/> — the day the CIMA LBC/FT text says the file's information is to
    /// be brought up to date (« les informations sont mises à jour à la date anniversaire du
    /// contrat »).
    ///
    /// <para>
    /// <b>A date-driven lookup, not an enumeration of holdings.</b> It answers "which clients owe
    /// an update today", which is a few rows a night; this contract deliberately exposes no way to
    /// walk a tenant's portfolio, and the reconciliation run's own notes explain why.
    /// </para>
    ///
    /// <para>
    /// The anniversary is measured from the holding's effective date, falling back to its
    /// signature: a contract that never recorded an effective date still began on the day the
    /// client signed it. A holding with neither has no anniversary and is skipped rather than
    /// given an arbitrary one — a review raised on a date nobody agreed to would be noise a
    /// compliance officer learns to ignore.
    /// </para>
    ///
    /// <para>
    /// 29 February is folded onto 28 February in a non-leap year, so a contract signed on a leap
    /// day is reviewed every year rather than once every four.
    /// </para>
    /// </summary>
    Task<IReadOnlyList<Guid>> GetClientsWithContractAnniversaryAsync(
        Guid tenantId, DateOnly onDay, CancellationToken ct);

    /// <summary>
    /// Returns a lightweight summary of a customer, or null if not found.
    /// </summary>
    Task<CustomerSummary?> GetCustomerAsync(Guid tenantId, Guid customerId, CancellationToken ct);

    /// <summary>
    /// Finds the live clients matching any of the identifiers supplied, so a captured lead can be
    /// recognised as a request from a customer already on file (BNF-B-22).
    ///
    /// <para>
    /// <b>The values arrive in CLEAR, and that is not an oversight.</b> Each module has its own
    /// blind-index key — M13's e-mail index and M01's are different hashes of the same address —
    /// so a caller cannot compute an index M01 can match. Handing over the raw value and letting
    /// M01 index it is the only shape that works, and it is the same reason
    /// <see cref="CreateFromLeadRequest"/> carries clear text. Nothing is persisted and nothing is
    /// echoed back: the result names clients, never their data.
    /// </para>
    ///
    /// <para>
    /// Matching is EXACT, per identifier, over live clients only (an archived or merged record is
    /// not a customer making a request). Fuzzy matching is M01's own duplicate scorer and stays
    /// there — a lead must not be attached to a client on a phonetic resemblance.
    /// </para>
    ///
    /// <para>
    /// Returns every match, and the caller decides: BNF-B-22 allows an automatic attachment only
    /// for a SINGLE exact match. Two clients sharing a phone is a real situation — a family
    /// handset — and the answer there is a question for an agent, not a silent choice between them.
    /// </para>
    /// </summary>
    /// <param name="phoneNumber">Any format; normalised by M01 before indexing.</param>
    /// <param name="email">Any casing.</param>
    /// <param name="identityDocumentNumber">Optional and the strongest signal when present.</param>
    Task<IReadOnlyList<CustomerMatch>> FindMatchesAsync(
        Guid tenantId,
        string? phoneNumber,
        string? email,
        string? identityDocumentNumber,
        CancellationToken ct);

    /// <summary>
    /// Opens a DRAFT product holding for a client, from a converted lead (BNF-B-24).
    ///
    /// <para>
    /// Called by M13 at conversion, once, when the lead carries a resolved product line. The
    /// holding starts in <c>Draft</c> — nothing is promised and nothing is sent — because the
    /// signature is a separate, deliberate act (BNF-B-16) and a conversion must not produce a
    /// contract the client has not seen.
    /// </para>
    ///
    /// <para>
    /// <b>Idempotent on <paramref name="sourceLeadId"/>.</b> A lead converts once, but the
    /// conversion is reachable from a retried request, so a second call returns the holding the
    /// first produced rather than opening a second subscription for the same request.
    /// </para>
    /// </summary>
    /// <returns>
    /// The holding's id, or a failure code. <c>PRODUCT_LINE_NOT_FOUND</c> when the line is not in
    /// the tenant's catalogue, <c>HOLDING_ALREADY_HELD</c> when the line forbids multiple holdings
    /// and the client has a live one — which the CALLER must not treat as a conversion failure: the
    /// client exists and the lead converted, and only the draft holding did not open.
    /// </returns>
    Task<Result<Guid>> CreateHoldingFromLeadAsync(
        CreateHoldingFromLeadRequest request, CancellationToken ct);

    /// <summary>
    /// Opens a DRAFT product holding for a client, from a WON commercial request.
    ///
    /// <para>
    /// The twin of <see cref="CreateHoldingFromLeadAsync"/>, for the other way a client ends up
    /// owed a contract: not a prospect being converted, but an existing client whose request an
    /// agent has just closed as won. Same draft, same reason — the signature is a separate,
    /// deliberate act (BNF-B-16), so closing a request must not produce a contract the client has
    /// not seen.
    /// </para>
    ///
    /// <para>
    /// <b>Idempotent on <c>SourceOpportunityId</c>.</b> A request is won once, but the closing is
    /// reachable from a retried call, so a second one returns the holding the first produced
    /// rather than opening a second subscription for the same request.
    /// </para>
    /// </summary>
    /// <returns>
    /// The holding's id, or a failure code — the same three as the lead path, and
    /// <c>HOLDING_ALREADY_HELD</c> is likewise NOT a failure for the caller: the request is won
    /// either way, and only the draft holding did not open.
    /// </returns>
    Task<Result<Guid>> CreateHoldingFromOpportunityAsync(
        CreateHoldingFromOpportunityRequest request, CancellationToken ct);

    /// <summary>
    /// Everything a back office needs to receive a subscription (BNF-B-16) — the terms, the clause
    /// as it stands, and the signature's existence.
    ///
    /// <para>
    /// Exists because the Integration module consumes <c>ProductSubscriptionRequestedEvent</c>,
    /// which carries IDS ONLY: the terms and the designations stay in M01, and an outbox row
    /// holding who receives a capital would be a copy of the contract that nothing ever purges. So
    /// the consumer reads them here instead, once, at the moment it needs them.
    /// </para>
    ///
    /// <para>
    /// Returns <c>null</c> when the holding does not exist in that tenant. The caller is an
    /// at-least-once event consumer and must not fail on a holding somebody closed in between.
    /// </para>
    /// </summary>
    Task<HoldingForSync?> GetHoldingForSyncAsync(
        Guid tenantId, Guid holdingId, CancellationToken ct);

    /// <summary>
    /// The back office accepted the subscription (BNF-B-16): the holding becomes <c>Active</c> and
    /// records the reference it answered with.
    /// </summary>
    /// <remarks>
    /// Idempotent in the way an at-least-once consumer needs: confirming a holding that is already
    /// <c>Active</c> with the same reference succeeds and changes nothing. A DIFFERENT reference is
    /// refused — two external references for one contract means one of the two systems is wrong,
    /// and overwriting would quietly pick a side.
    /// </remarks>
    Task<Result> ConfirmHoldingSyncAsync(
        Guid tenantId, Guid holdingId, string externalSystem, string externalRef, CancellationToken ct);

    /// <summary>
    /// The back office refused it (BNF-B-16). The holding becomes <c>Rejected</c> and carries a
    /// reason an agent can act on — which is why the reason is required and not optional.
    /// </summary>
    Task<Result> RejectHoldingSyncAsync(
        Guid tenantId, Guid holdingId, string reason, CancellationToken ct);

    /// <summary>
    /// The sync state of several holdings at once (BNF-B-17) — what a reconciliation run compares
    /// against the back office's own record.
    ///
    /// <para>
    /// A BATCHED read, and the only one that suits a sweep: <see cref="GetHoldingForSyncAsync"/>
    /// returns the whole contract including its clause and makes a catalogue call per holding,
    /// which is right for sending one subscription and ruinous for comparing ten thousand. This
    /// returns four scalars and touches nothing else.
    /// </para>
    ///
    /// <para>
    /// Keep the batch bounded to a page's worth of ids: the implementation turns them into a
    /// single SQL <c>IN</c>, which stops being a favour somewhere in the thousands — the same
    /// caveat <see cref="GetClientSummariesAsync"/> carries.
    /// </para>
    ///
    /// <para>
    /// A holding the tenant does not have is simply ABSENT from the result rather than present
    /// with nulls. The caller is a comparison, and "the CRM has no such holding" is a finding it
    /// must be able to tell from "the CRM has it and it is inactive".
    /// </para>
    /// </summary>
    Task<IReadOnlyDictionary<Guid, HoldingSyncState>> GetHoldingSyncStatesAsync(
        Guid tenantId, IReadOnlyCollection<Guid> holdingIds, CancellationToken ct);
}

/// <summary>
/// What a reconciliation needs to know about one holding (BNF-B-17). Four scalars, no clause and
/// no personal data — a nightly comparison has no business reading who is designated.
/// </summary>
/// <param name="ClauseVersion">
/// Which version of the beneficiary clause the CRM holds. Compared against the latest version the
/// back office was actually SENT, which is how "the insurer holds an older clause than we do" is
/// detected.
/// </param>
/// <param name="IsLive">
/// Whether the CRM considers this holding in force. Computed by M01 from its own status rather
/// than left to a caller to re-derive from the string — the same reasoning as
/// <c>HoldingDto.CanEdit</c>.
/// </param>
public sealed record HoldingSyncState(
    Guid HoldingId,
    string Status,
    string? ExternalRef,
    int ClauseVersion,
    bool IsLive);

/// <summary>
/// A holding as a back office receives it (BNF-B-16).
/// </summary>
/// <param name="IsSigned">
/// Whether the client's signature is recorded. The CALLER re-checks it: the event was published
/// inside the transaction that moved the holding to <c>PendingSync</c>, and that transition already
/// required a signature — but a consumer that pushes a contract to an insurer must not take a
/// second-hand word for it.
/// </param>
/// <param name="ExternalSystem">
/// The <c>IntegrationKind</c> name the product line is pushed to, from its policy. Null means the
/// line is CRM-only and nothing should be sent at all.
/// </param>
/// <param name="Designations">The clause AS IT STANDS, at <paramref name="ClauseVersion"/>.</param>
public sealed record HoldingForSync(
    Guid HoldingId,
    Guid ClientId,
    Guid ProductLineId,
    string ProductCode,
    string ProductCategory,
    string Status,
    int ClauseVersion,
    bool IsSigned,
    DateTimeOffset? SignedAt,
    string? SignatureReference,
    string? ExternalSystem,
    decimal? InsuredCapital,
    decimal? Premium,
    string? Periodicity,
    string? Currency,
    DateOnly? EffectiveFrom,
    IReadOnlyList<HoldingDesignationForSync> Designations);

/// <summary>
/// One designated person as a back office receives them (BNF-B-16).
/// </summary>
/// <param name="FullName">
/// Resolved: a client's display name, a household member's name, or the name typed for an external
/// person. An insurer needs a name on a beneficiary clause — this is the one place a designation's
/// subject crosses a module boundary as words rather than as an id, and it goes no further than the
/// adapter that sends it.
/// </param>
public sealed record HoldingDesignationForSync(
    string RoleCode,
    string FullName,
    string? Relationship,
    int Rank,
    decimal? SharePercent,
    DateOnly? BirthDate);

/// <summary>
/// One client matching a lead's identifiers (BNF-B-22). Carries no sensitive field: the point is to
/// name the record, and a caller that needs more reads it through M01's own audited endpoints.
/// </summary>
/// <param name="MatchedOn">
/// Which identifiers matched — <c>"phone"</c>, <c>"email"</c>, <c>"identityDocument"</c>. Several
/// when several did. The agent confirming a suggestion needs to see WHY it was suggested, and a
/// bare confidence number does not answer that.
/// </param>
/// <param name="SourceLeadId">
/// What makes the call idempotent. The holding records it, so a retry finds the existing one.
/// </param>
public sealed record CreateHoldingFromLeadRequest(
    Guid TenantId,
    Guid ClientId,
    Guid ProductLineId,
    Guid SourceLeadId,
    Guid ConvertedByUserId);

/// <param name="SourceOpportunityId">
/// What makes the call idempotent. The holding records it, so a retry finds the existing one.
/// </param>
/// <param name="ClosedByUserId">
/// The agent who closed the request as won — the act that produced the draft, and so the author
/// the audit trail must name.
/// </param>
public sealed record CreateHoldingFromOpportunityRequest(
    Guid TenantId,
    Guid ClientId,
    Guid ProductLineId,
    Guid SourceOpportunityId,
    Guid ClosedByUserId);

public sealed record CustomerMatch(
    Guid ClientId,
    string ClientNumber,
    string DisplayName,
    Guid AgencyId,
    Guid? AdvisorUserId,
    string Status,
    string KycStatus,
    IReadOnlyList<string> MatchedOn);

/// <summary>
/// Non-sensitive client projection. Enum-typed domain concepts are exposed as strings
/// so no consumer has to reference the Customers domain assembly.
/// </summary>
/// <summary>
/// A templated notification addressed to one of the tenant's clients (see
/// <see cref="ICustomersModule.NotifyClientAsync"/>).
/// </summary>
/// <param name="TemplateKey">
/// Key of a seeded email template. An unknown key is a failed send, not an exception.
/// </param>
/// <param name="IdempotencyKey">
/// Deduplicates the message in M08. Key it on what the notification is ABOUT — the invitation, the
/// decision — rather than on the broker's message id, so a replayed event cannot produce a second
/// mail even if an inbox table was recreated.
/// </param>
/// <param name="TemplateData">
/// Values the template needs. <c>full_name</c>, <c>client_first_name</c> and <c>company_name</c>
/// are added by this module. Must carry NO sensitive value and no identity read off a document: a
/// rendered body travels to a mail provider and sits in the delivery log. A single-use link is the
/// one exception and it is the point of the first caller — it is a credential the recipient already
/// holds, not a fact about them.
/// </param>
/// <summary>
/// A templated text message addressed to one of the tenant's clients (see
/// <see cref="ICustomersModule.NotifyClientBySmsAsync"/>).
/// </summary>
/// <param name="TemplateKey">
/// An SMS template key. SMS and email keys are SEPARATE sets: the same event often needs both,
/// and sharing a key would make one the other's accidental fallback.
/// </param>
/// <param name="TemplateData">
/// Values for the body. This module adds <c>client_first_name</c> and <c>company_name</c> after
/// the caller's own, so a caller cannot address somebody else's customer by name. Deliberately
/// fewer than the email path adds: a text message sits unlocked on a lock screen, so the bodies
/// are written to need as little identity as possible.
/// </param>
/// <param name="IdempotencyKey">
/// A duplicate is dropped and reported as SUCCESS — the caller asked for the customer to have
/// this message and they do. On a per-message channel that is also what stops a retried consumer
/// from charging the tenant twice.
/// </param>
public sealed record NotifyClientBySmsRequest(
    Guid TenantId,
    Guid ClientId,
    string TemplateKey,
    string IdempotencyKey,
    IReadOnlyDictionary<string, object>? TemplateData = null);

public sealed record NotifyClientRequest(
    Guid TenantId,
    Guid ClientId,
    string TemplateKey,
    string IdempotencyKey,
    IReadOnlyDictionary<string, object>? TemplateData = null);

public sealed record ClientSummary(
    Guid Id,
    string ClientNumber,
    string ClientType,
    string DisplayName,
    string Status,
    Guid AgencyId,
    Guid? AdvisorUserId,
    string KycStatus,
    string RiskLevel,
    Guid? MergedIntoId);

/// <summary>
/// Lead-to-client conversion payload (US-M01-BE-06). Sensitive values arrive in clear
/// text from the Leads module and are encrypted by module M01 before persistence;
/// they are never echoed back.
/// </summary>
public sealed record CreateFromLeadRequest(
    Guid TenantId,
    Guid LeadId,
    Guid AgencyId,
    Guid ConvertedByUserId,
    string? FirstName,
    string? LastName,
    string? LegalName,
    string? Gender,
    DateOnly? DateOfBirth,
    string? Nationality,
    string? PhoneNumber,
    string? Email,
    string? IdentityDocumentType,
    string? IdentityDocumentNumber,
    string? Profession,
    string? PreferredLanguage,
    Guid? RequestedClientId);

/// <summary>
/// <c>AlreadyExisted</c> is true when the lead had already been converted — the
/// unique filtered index on (tenant, source_lead_id) is what makes this safe.
///
/// <c>BlockingCode</c> is set when conversion could not create a NEW client because an
/// existing record blocks it (a duplicate identity document, for instance): the code is
/// one of the M01 error codes and <c>ClientId</c> then points at the blocking client, so
/// the Leads module can show the operator which record to reconcile instead of just
/// reporting a failure. It stays null on a normal creation.
/// </summary>
public sealed record CreateFromLeadResult(
    Guid ClientId,
    string ClientNumber,
    bool AlreadyExisted,
    string? BlockingCode = null);


public sealed record CustomerSummary(
    Guid Id,
    string DisplayName,
    string? Email,
    string? PhoneNumber,
    Guid? AgencyId);