namespace Sankore.Modules.Integration.PublicApi;

using Sankore.Shared.Kernel;

/// <summary>
/// The module's whole public surface: three gateways, one per family of back-office (INT-02,
/// ASS-02, TEL-B-02).
///
/// <para>
/// A consumer module (M01, M02, M13, and the insurance screens) depends on THIS and never on an
/// adapter. The point is not tidiness — it is that the IMF's CBS is chosen by the IMF: the same
/// SANKORE deployment serves a Temenos tenant and a batch-file tenant, and no caller may contain
/// a branch on which.
/// </para>
/// </summary>
public interface IIntegrationModule
{
    ICoreBankingGateway CoreBanking { get; }

    IInsuranceGateway Insurance { get; }

    /// <summary>
    /// Telephony (TEL-B-02). Never null, like its siblings: a tenant with no telephony connection
    /// gets a gateway that answers "no active connection", rather than a null property every
    /// caller would have to test before using.
    /// </summary>
    ITelephonyGateway Telephony { get; }

    /// <summary>
    /// The WhatsApp channel (WA-BE-01, WA-BE-02). Never null, like its siblings: a tenant with no
    /// number gets answers that say so.
    /// </summary>
    IMessagingGateway Messaging { get; }
}

/// <summary>
/// The institution's WhatsApp numbers, as M13 sees them: which number writes to a lead, what it
/// may send, and the send itself.
///
/// <para>
/// <b>Answers now or fails now</b>, like the telephony gateway and unlike core banking: M13
/// queues the message in its own outbox and calls this from the consumer, so the queue, the retry
/// decision and the message's status are M13's. A provider's error number never crosses this
/// contract — only the <c>MESSAGING_*</c> codes of <see cref="IntegrationErrors"/>.
/// </para>
///
/// <para>
/// <b>The tenant is a parameter on every member</b>: the callers are an outbox consumer and a
/// Hangfire job, where the ambient identity is SYSTEM.
/// </para>
/// </summary>
public interface IMessagingGateway
{
    /// <summary>
    /// The ACTIVE number that writes to a lead of this agency — the agency's own, else the
    /// institution-wide one — or null when the channel is off for it.
    /// </summary>
    Task<MessagingChannelInfo?> ResolveChannelAsync(Guid tenantId, Guid? agencyId, CancellationToken ct);

    /// <summary>One number of the tenant, active or not, or null.</summary>
    Task<MessagingChannelInfo?> GetChannelAsync(Guid tenantId, Guid connectionId, CancellationToken ct);

    /// <summary>The tenant's numbers, active or not.</summary>
    Task<IReadOnlyList<MessagingChannelInfo>> ListChannelsAsync(Guid tenantId, CancellationToken ct);

    /// <summary>The templates of one number, whatever their approval status.</summary>
    Task<IntegrationResult<IReadOnlyList<MessagingTemplateInfo>>> ListTemplatesAsync(
        Guid tenantId, Guid connectionId, CancellationToken ct);

    /// <summary>Sends an approved template. Answers the provider's message id.</summary>
    Task<IntegrationResult<string>> SendTemplateAsync(
        Guid tenantId, Guid connectionId, MessagingTemplateSend message, CancellationToken ct);

    /// <summary>Sends free text, inside the 24-hour window. Answers the provider's message id.</summary>
    Task<IntegrationResult<string>> SendTextAsync(
        Guid tenantId, Guid connectionId, string toE164, string text, CancellationToken ct);

    /// <summary>
    /// Uploads and sends an image or a PDF, inside the 24-hour window. Answers the message id and
    /// the media id the thread shows the file again by.
    /// </summary>
    Task<IntegrationResult<MessagingMediaSent>> SendMediaAsync(
        Guid tenantId, Guid connectionId, string toE164, MessagingMediaContent media, string? caption, CancellationToken ct);

    /// <summary>The bytes of a media of the conversation, at most 10 MB.</summary>
    Task<IntegrationResult<MessagingMediaContent>> DownloadMediaAsync(
        Guid tenantId, Guid connectionId, string mediaId, CancellationToken ct);

    /// <summary>
    /// Deletes a media at the provider (WA-BE-21). Best effort for the caller: a failure is
    /// reported, never thrown, and a media already gone is a success.
    /// </summary>
    Task<IntegrationResult> DeleteMediaAsync(
        Guid tenantId, Guid connectionId, string mediaId, CancellationToken ct);
}

/// <param name="Kind">The provider, e.g. <c>Dialog360</c>.</param>
/// <param name="DisplayPhoneNumber">The number as customers see it. Not a secret.</param>
/// <param name="AgencyId">The agency it serves; null for the institution-wide number.</param>
public sealed record MessagingChannelInfo(
    Guid ConnectionId,
    string Name,
    string Kind,
    string? DisplayPhoneNumber,
    Guid? AgencyId,
    bool IsActive);

/// <param name="Status">
/// The provider's word, upper-cased: <c>APPROVED</c>, <c>PENDING</c>, <c>REJECTED</c>,
/// <c>PAUSED</c>, <c>DISABLED</c>…
/// </param>
public sealed record MessagingTemplateInfo(
    string? ProviderTemplateId,
    string Name,
    string Language,
    string Category,
    string Status,
    string? HeaderText,
    string BodyText,
    string? FooterText,
    IReadOnlyList<string> Buttons);

/// <param name="ToE164">The recipient, with the plus. Personal data.</param>
/// <param name="BodyParameters">The values of <c>{{1}}</c>, <c>{{2}}</c>… in order. Personal data.</param>
public sealed record MessagingTemplateSend(
    string ToE164,
    string Name,
    string Language,
    IReadOnlyList<string> BodyParameters)
{
    /// <summary>The template only: the generated form would print the number and the values.</summary>
    public override string ToString() => $"MessagingTemplateSend {{ {Name} ({Language}) }}";
}

/// <summary>The bytes of one media and what they are.</summary>
public sealed record MessagingMediaContent(byte[] Content, string ContentType, string? FileName);

/// <summary>A media the provider accepted: the message id, and the uploaded file's id.</summary>
public sealed record MessagingMediaSent(string ProviderMessageId, string MediaId);

/// <summary>
/// Core banking, as the rest of the platform sees it.
///
/// <para>
/// The three <c>Request…Async</c> methods return an <see cref="IntegrationCommandId"/> and not a
/// result: they enqueue, inside the caller's own transaction, and the dispatcher delivers
/// out of band. A client validated at 23:00 while the CBS is down must still be created when it
/// comes back, and the agent must not be made to wait for it (INT-05).
/// </para>
///
/// <para>
/// The two read methods answer now, from the snapshot or from the CBS, and may legitimately
/// return <c>null</c>: a customer the CBS has never heard of is an ordinary state, not an error.
/// </para>
/// </summary>
public interface ICoreBankingGateway
{
    Task<IntegrationCommandId> RequestCustomerCreationAsync(Guid crmCustomerId, CancellationToken ct);

    Task<IntegrationCommandId> RequestKycLevelUpdateAsync(Guid crmCustomerId, KycLevel level, CancellationToken ct);

    Task<IntegrationCommandId> RequestAccountOpeningAsync(Guid crmCustomerId, string productCode, CancellationToken ct);

    /// <summary>
    /// Queues the debit of an insurance premium against a customer's account — the FIRST step of
    /// the ASS-05 chain, before anything is asked of the insurer.
    ///
    /// <para>
    /// That order is the whole design: a premium collected and a policy refused can be put right
    /// by a reversal, while a policy issued against a premium that was never collected is an
    /// insurer's receivable against a customer who was never debited. The reversal is the
    /// compensating step, not an afterthought.
    /// </para>
    ///
    /// <para>
    /// Idempotent on the SUBSCRIPTION, so a retried request attaches to the debit already in
    /// flight rather than taking the money twice.
    /// </para>
    /// </summary>
    Task<IntegrationCommandId> RequestPremiumDebitAsync(
        PremiumDebitRequest request, CancellationToken ct);

    /// <summary>
    /// Queues the REVERSAL of a premium debit, keyed on the reference the original debit returned
    /// — the only form a core banking system will safely accept twice.
    ///
    /// <para>
    /// <paramref name="request"/> carries the attempt number because a refused reversal is NOT
    /// retried automatically: the subscription sits in <c>PremiumRefundDue</c> until an operator
    /// re-attempts, and without the attempt in the idempotency key that second attempt would be
    /// swallowed as a replay — leaving a customer un-refunded while the platform reports a refund
    /// as requested.
    /// </para>
    /// </summary>
    Task<IntegrationCommandId> RequestDebitReversalAsync(
        DebitReversalRequest request, CancellationToken ct);

    /// <summary>
    /// Queues one attempt at collecting one premium INSTALMENT (ASS-08).
    ///
    /// <para>
    /// Separate from <see cref="RequestPremiumDebitAsync"/>, which collects the premium of a
    /// SUBSCRIPTION and is idempotent on it so a retried request cannot debit twice. An instalment
    /// is retried on purpose, on the product's own schedule, so its key carries the attempt — each
    /// one is a genuine second ask of the core banking system.
    /// </para>
    /// </summary>
    Task<IntegrationCommandId> RequestInstalmentDebitAsync(
        InstalmentDebitRequest request, CancellationToken ct);

    Task<CbsCustomerSnapshot?> GetCustomerSnapshotAsync(Guid crmCustomerId, CancellationToken ct);

    Task<CbsBalance?> GetLiveBalanceAsync(Guid crmCustomerId, string accountRef, CancellationToken ct);

    /// <summary>Capabilities of the current tenant's active core-banking connection.</summary>
    IntegrationCapabilities GetCapabilities();
}

/// <summary>
/// Insurance, as the rest of the platform sees it (ASS-02).
///
/// <para>
/// Same shape as <see cref="ICoreBankingGateway"/> on purpose — writes are queued commands,
/// reads answer from a read model — but <see cref="GetCapabilities"/> takes a connection id:
/// a tenant may distribute for several insurers at once, each with its own adapter and its own
/// supported operations.
/// </para>
/// </summary>
public interface IInsuranceGateway
{
    Task<IntegrationCommandId> RequestPolicySubscriptionAsync(
        InsurancePolicyPayload payload, CancellationToken ct);

    Task<IReadOnlyList<InsurancePolicy>> GetPoliciesAsync(Guid crmCustomerId, CancellationToken ct);

    Task<IntegrationCommandId> RequestClaimDeclarationAsync(
        InsuranceClaimPayload payload, CancellationToken ct);

    Task<IReadOnlyList<InsuranceClaim>> GetClaimsAsync(Guid crmCustomerId, CancellationToken ct);

    /// <summary>
    /// Queues one <i>pièce de sinistre</i> for the insurer (ASS-09).
    ///
    /// <para>
    /// The connection is derived from the dossier's own insurer reference rather than passed, the
    /// same way <see cref="RequestClaimDeclarationAsync"/> derives it: a caller holding a document
    /// id has no business choosing which insurer receives it.
    /// </para>
    /// </summary>
    Task<IntegrationCommandId> RequestClaimDocumentAsync(
        ClaimDocumentPayload payload, CancellationToken ct);

    IntegrationCapabilities GetCapabilities(Guid connectionId);
}

/// <summary>
/// Telephony, as the rest of the platform sees it (TEL-B-02).
///
/// <para>
/// <b>Reads only, and the two agent ACTS are deliberately absent.</b> Placing a call and
/// correcting an attachment were on this contract when the family was scaffolded; they are gone
/// because both need an authenticated actor whose perimeter the owning module verifies — the
/// number resolution of TEL-B-06 refuses an actor that is not the ambient one, precisely so that
/// nothing can ask for a perimeter check it then does not make. A module calling on an agent's
/// behalf has no such actor to offer, and no caller ever wanted to: the two live on the HTTP
/// surface, under <c>Telephony.Call.Make</c> and <c>Telephony.Call.Link</c>, where the permission
/// and the audit row are.
/// </para>
///
/// <para>
/// <b>It does not look like the other two gateways, and the difference is the point.</b> A write
/// to a back office is queued as a command and answered later; a telephone call is not. An agent
/// clicking "call" waits for their own handset to ring, so <see cref="RequestClickToCallAsync"/>
/// answers now or fails now — there is no command id to follow, and a retry would make a phone
/// ring twice in somebody's hand.
/// </para>
///
/// <para>
/// No method here takes or returns a telephone NUMBER. The caller names a record and which of its
/// numbers to use; the number is decrypted inside this module and never crosses back out. That is
/// what keeps a number out of a browser, a log and an audit payload at once.
/// </para>
/// </summary>
public interface ITelephonyGateway
{
    /// <summary>
    /// What is known about one call, for the screen that opens while a telephone is ringing.
    /// Null when the call does not exist for this tenant.
    ///
    /// <para>
    /// Carries NO telephone number, by construction: it names people. The realtime push that
    /// precedes it carries a call id and nothing else, which is the half of TEL-B-10 that keeps a
    /// customer's name out of a websocket frame nobody re-authorises.
    /// </para>
    /// </summary>
    Task<TelephonyCallContext?> GetCallContextAsync(Guid callId, CancellationToken ct);

    /// <summary>The calls of one record, newest first. Feeds a client's or a lead's history.</summary>
    Task<IReadOnlyList<TelephonyCallSummary>> GetCallsForEntityAsync(
        TelephonyEntityRef entity, int page, int pageSize, CancellationToken ct);

    /// <summary>
    /// What the tenant's provider can do. <c>None</c> when no telephony connection is active,
    /// which is how a caller tells "switched off" from "cannot do that".
    /// </summary>
    IntegrationCapabilities GetCapabilities();
}

/// <summary>
/// Which record a call is about. An opaque pair — the type as a string, the id as a Guid — so no
/// module has to reference another's domain to name one.
/// </summary>
public sealed record TelephonyEntityRef(string Type, Guid Id);

/// <summary>
/// Which of a record's numbers to dial. <b>A slot and not a number</b>: the front names the slot,
/// the server decrypts the number. A contract taking a number would be a contract a browser can
/// be made to post somebody else's number to.
/// </summary>
public enum TelephonyPhoneSlot
{
    Primary,
    Secondary,
}

/// <param name="Candidates">
/// Who the remote number might be. Several is an ordinary answer — a family handset, a shop line
/// — and the screen asks the agent rather than guessing.
/// </param>
public sealed record TelephonyCallContext(
    Guid CallId,
    string Direction,
    string Status,
    string MatchStatus,
    IReadOnlyList<TelephonyCandidate> Candidates,
    DateTimeOffset StartedAt);

/// <param name="OutOfPerimeter">
/// True when the record exists but belongs to another agency. The name is then withheld and the
/// screen says only that the caller is known elsewhere — the perimeter hides the record, not the
/// fact that the number is known, which is what stops an agent searching for nothing.
/// </param>
public sealed record TelephonyCandidate(
    string EntityType,
    Guid EntityId,
    string? DisplayName,
    bool OutOfPerimeter);

public sealed record TelephonyCallSummary(
    Guid CallId,
    string Direction,
    string Status,
    DateTimeOffset StartedAt,
    int? TalkSeconds,
    string? OutcomeCode,
    Guid? CrmUserId);

/// <param name="SubscriptionId">
/// The subscription this premium belongs to. It is the idempotency anchor AND what the command
/// row carries as its CRM id, so a command found in the rejection queue says what it was for.
/// </param>
/// <param name="AccountRef">The account to debit, as the core banking system knows it.</param>
/// <param name="Label">
/// What appears on the customer's statement. Operator-facing text, never personal data: it reaches
/// a core banking system this platform does not control.
/// </param>
public sealed record PremiumDebitRequest(
    Guid SubscriptionId,
    string AccountRef,
    decimal Amount,
    string Currency,
    string Label);

/// <param name="OriginalReference">
/// The reference the DEBIT returned. A reversal is keyed on it rather than on the account and the
/// amount, because that is the only form a core banking system will accept twice without the risk
/// of refunding twice.
/// </param>
/// <param name="Attempt">
/// Which attempt this is, starting at 1. Part of the idempotency key — see
/// <see cref="ICoreBankingGateway.RequestDebitReversalAsync"/>.
/// </param>
public sealed record DebitReversalRequest(
    Guid SubscriptionId,
    string AccountRef,
    string OriginalReference,
    int Attempt);

/// <param name="Attempt">
/// Which attempt this is, starting at 1. Part of the idempotency key — see
/// <see cref="ICoreBankingGateway.RequestInstalmentDebitAsync"/>.
/// </param>
/// <param name="Label">
/// What appears on the customer's statement. Operator-facing, never personal data: it reaches a
/// core banking system this platform does not control.
/// </param>
public sealed record InstalmentDebitRequest(
    Guid InstalmentId,
    string AccountRef,
    decimal Amount,
    string Currency,
    int Attempt,
    string Label);
