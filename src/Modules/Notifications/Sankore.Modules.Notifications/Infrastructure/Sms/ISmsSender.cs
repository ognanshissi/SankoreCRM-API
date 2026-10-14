namespace Sankore.Modules.Notifications.Infrastructure.Sms;

/// <summary>
/// Low-level SMS transport. One implementation per provider, selected by
/// <c>CompositeSmsSender</c> from the resolved configuration.
/// </summary>
internal interface ISmsSender
{
    /// <summary>
    /// Hands one message to the provider and returns what it said.
    ///
    /// <para>
    /// Throws on refusal. The outbox processor treats an exception as a failed attempt and
    /// retries, then dead-letters — which is the behaviour a transient provider error needs, and
    /// is also why an implementation must never swallow a rejection and return normally: that
    /// would record a message as sent that was not.
    /// </para>
    /// </summary>
    Task<SmsSendResult> SendAsync(
        SendSmsRequest request, ResolvedSmsProvider provider, CancellationToken ct);
}

/// <summary>A fully resolved send — the body is already rendered, no template data left.</summary>
internal sealed record SendSmsRequest(
    Guid MessageId,
    Guid TenantId,
    string ToPhone,
    string Body);

/// <param name="ProviderMessageId">
/// What the provider called this message. Stored on the outbox row, and the only thing a delivery
/// callback can be correlated on. Null when a provider does not report one, which is a provider
/// whose delivery reports this platform cannot place.
/// </param>
/// <param name="ProviderStatus">
/// The provider's own first word on it — <c>queued</c>, <c>accepted</c>, <c>sent</c>. Verbatim.
/// </param>
internal sealed record SmsSendResult(string? ProviderMessageId, string? ProviderStatus);
