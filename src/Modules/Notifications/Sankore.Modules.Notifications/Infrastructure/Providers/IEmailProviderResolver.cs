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
/// Credentials are never stored here — only the vault reference path.
/// </summary>
public sealed record ResolvedEmailProvider(
    string ProviderType,
    bool IsDefault,
    string? FromEmail,
    string? FromName,
    string? ReplyToEmail,
    string? SendingDomain,
    string? CredentialVaultPath);
