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