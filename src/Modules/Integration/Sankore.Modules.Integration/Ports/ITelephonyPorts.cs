namespace Sankore.Modules.Integration.Ports;

using Sankore.Modules.Integration.Domain;
using Sankore.Modules.Integration.PublicApi;

/// <summary>
/// What a telephony provider can be asked to do (TEL-B-02).
///
/// <para>
/// <b>One port and not the four-interface split the other families use</b>, because the
/// operations here are not independent groups: an adapter that lists users but cannot place a
/// call is not a telephony provider. What varies between providers is answered by
/// <c>IntegrationCapability</c> — recordings, deletion at the provider, webhooks — and an
/// adapter that does not claim a capability simply answers
/// <c>IntegrationErrors.CapabilityNotSupported</c> on it.
/// </para>
///
/// <para>
/// The adapter implementing this also implements <c>ICbsAdapter</c>, which is the base every
/// family's adapter implements despite its name — <c>Kind</c>, <c>CapabilitiesFor</c> and
/// <c>CheckHealthAsync</c> are family-agnostic, and the insurance family already does the same.
/// That is what lets <c>IntegrationAdapterResolver.ResolvePort</c> work here unchanged.
/// </para>
/// </summary>
public interface ITelephonyPort
{
    /// <summary>
    /// The provider's own users, so CRM accounts can be linked to them (TEL-B-05).
    /// </summary>
    Task<IntegrationResult<IReadOnlyList<ProviderUser>>> ListUsersAsync(CancellationToken ct);

    /// <summary>
    /// Places an outbound call by ringing the AGENT first, then dialling the target (TEL-B-06).
    ///
    /// <para>
    /// No <c>IdempotencyKey</c>, unlike every write of the other families, and that is deliberate:
    /// a retried call is not a duplicate record to reconcile, it is a telephone ringing twice in
    /// somebody's hand. The caller does not retry — see the click-to-call slice.
    /// </para>
    /// </summary>
    Task<IntegrationResult<ExternalId>> StartCallbackAsync(
        ExternalId providerUserId, string targetNumber, string? callerNumber, CancellationToken ct);

    /// <summary>
    /// One call, re-read from the provider. <b>This is what makes a webhook trustworthy</b>: the
    /// event says something happened, this says what.
    /// </summary>
    Task<IntegrationResult<ProviderCall>> GetCallAsync(
        ExternalId providerCallId, CancellationToken ct);

    /// <summary>
    /// Calls since an instant, paged. Feeds the catch-up sweep that finds the calls whose webhook
    /// was lost (TEL-B-14).
    /// </summary>
    Task<IntegrationResult<ProviderCallPage>> ListCallsAsync(
        DateTimeOffset from, string? cursor, CancellationToken ct);

    /// <summary>
    /// The recording's bytes. Returned whole rather than as a stream, and the reason is the same
    /// one the claim-document store states: AES-GCM is single-shot in .NET, so the plaintext is
    /// materialised whatever this signature says, and promising incrementality here would be a
    /// lie the caller would size its memory against.
    /// </summary>
    Task<IntegrationResult<ProviderRecording>> DownloadRecordingAsync(
        ExternalId providerCallId, CancellationToken ct);

    /// <summary>
    /// Deletes the provider's copy once SANKORE holds its own (TEL-B-16). Only called when the
    /// adapter claims <c>RecordingDeletion</c>; a provider without it is an ordinary provider,
    /// and the retention is then ours alone.
    /// </summary>
    Task<IntegrationResult> DeleteRecordingAsync(ExternalId providerCallId, CancellationToken ct);

    /// <summary>
    /// Translates a provider's webhook body into something this module understands.
    ///
    /// <para>
    /// <b>Pure — no I/O, no clock, no database.</b> It is called on an anonymous request before
    /// anything has been verified, and a parser that reached out would be a parser an unverified
    /// caller can make reach out. Everything it cannot determine locally is left null for the
    /// handler to resolve by re-reading the call.
    /// </para>
    /// </summary>
    ProviderWebhookEvent ParseWebhook(TelephonyWebhookRequest request);
}

/// <summary>One of the provider's users, as its directory lists them.</summary>
public sealed record ProviderUser(
    ExternalId Id,
    string? Email,
    string? DisplayName,
    bool IsActive,
    IReadOnlyList<string> Numbers);

/// <summary>
/// One call as the provider reports it — the authoritative reading.
///
/// <para>
/// Numbers arrive in CLEAR and are encrypted by the caller before anything is stored. They do not
/// travel further: no other module ever sees this type.
/// </para>
/// </summary>
public sealed record ProviderCall(
    ExternalId Id,
    TelCallDirection Direction,
    TelCallStatus Status,
    string FromNumber,
    string ToNumber,
    DateTimeOffset StartedAt,
    DateTimeOffset? AnsweredAt,
    DateTimeOffset? EndedAt,
    int? RingSeconds,
    int? TalkSeconds,
    ExternalId? ProviderUserId,
    bool HasRecording);

/// <summary>A page of calls, with the cursor to ask for the next one.</summary>
public sealed record ProviderCallPage(IReadOnlyList<ProviderCall> Calls, string? NextCursor);

/// <summary>A recording's bytes and what the provider says about them.</summary>
public sealed record ProviderRecording(
    byte[] Content, string ContentType, int? DurationSeconds);

/// <summary>
/// A webhook request, reduced to what a parser may look at. Deliberately not
/// <c>HttpRequest</c>: a pure function must not be handed something it could read a header from
/// twice, or a body it could consume.
/// </summary>
public sealed record TelephonyWebhookRequest(
    string Body, IReadOnlyDictionary<string, string> Headers);

/// <summary>
/// What a webhook said, as this module reads it.
///
/// <para>
/// <see cref="DedupKey"/> is the only member with no fallback: without it the event cannot be
/// made idempotent, and an adapter that cannot produce one should compose it from the call, the
/// type and the instant rather than leave it empty.
/// </para>
/// </summary>
public sealed record ProviderWebhookEvent(
    string DedupKey,
    string EventType,
    ExternalId? CallId,
    TelCallStatus? Status,
    DateTimeOffset? OccurredAt);
