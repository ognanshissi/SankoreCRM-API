namespace Sankore.Modules.Notifications.Infrastructure.Providers;

/// <summary>
/// Resolves the effective email provider configuration for a tenant.
/// Cached in Redis; falls back to Administration module on cache miss.
/// </summary>
public interface IEmailProviderResolver
{
    Task<ResolvedEmailProvider> ResolveAsync(Guid tenantId, CancellationToken ct = default);
}

/// <summary>
/// Effective provider configuration used by the outbox processor at send time.
///
/// It deliberately holds NO credential. This record is serialized into Redis, and a cache is the
/// last place an SMTP password or an API key should live: the sender fetches the secret from the
/// vault at send time, keyed by tenant and provider.
/// </summary>
public sealed record ResolvedEmailProvider(
    string ProviderType,
    bool IsDefault,
    string? FromEmail,
    string? FromName,
    string? ReplyToEmail,
    string? SendingDomain,
    /// <summary>A credential exists in the vault. Not the credential.</summary>
    bool HasCredential,
    string? SmtpHost,
    int? SmtpPort,
    string? SmtpUsername,
    bool SmtpUseSsl,
    bool SmtpUseStartTls);
