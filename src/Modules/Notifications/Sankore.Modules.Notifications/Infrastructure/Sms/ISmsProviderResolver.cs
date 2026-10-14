namespace Sankore.Modules.Notifications.Infrastructure.Sms;

/// <summary>
/// Resolves the effective SMS configuration of a tenant. Cached, falling back to M12 on a miss.
/// </summary>
public interface ISmsProviderResolver
{
    Task<ResolvedSmsProvider> ResolveAsync(Guid tenantId, CancellationToken ct = default);
}

/// <summary>
/// What the outbox processor needs at send time.
///
/// <para>
/// It holds NO credential, exactly like <c>ResolvedEmailProvider</c>: this record is serialised
/// into a distributed cache, and a cache is the last place an API key or an auth token should
/// live. The sender fetches the secret from the vault per send, keyed by tenant and provider.
/// </para>
///
/// <para>
/// <b>There is no platform default here, unlike the email resolver.</b> A tenant that configured
/// nothing resolves to <see cref="None"/>, whose <see cref="CanSend"/> is false — because an
/// email can always leave through the platform's own SMTP account while a text message cannot:
/// somebody pays per message, and no platform-wide SMS account exists.
/// </para>
/// </summary>
public sealed record ResolvedSmsProvider(
    string ProviderType,
    string? SenderId,
    string? AccountSid,
    bool HasCredential,
    bool CanSend)
{
    /// <summary>A tenant with no SMS channel. Everything a sender asks is answered "no".</summary>
    public static readonly ResolvedSmsProvider None = new(
        ProviderType: "Disabled",
        SenderId: null,
        AccountSid: null,
        HasCredential: false,
        CanSend: false);
}
