namespace Sankore.Shared.Kernel;

/// <summary>
/// Identifies a secret in the vault: (TenantId, Scope, EntityId, Name).
/// E.g. (tenantId, "LeadSource", sourceId, "hmac-signing").
/// </summary>
public sealed record SecretKey(Guid TenantId, string Scope, Guid EntityId, string Name);

/// <summary>
/// Read-only hint about a stored secret — never exposes the actual value.
/// </summary>
public sealed record SecretHint(string Name, string MaskedValue, DateTimeOffset? ExpiresAt);

/// <summary>
/// Secrets vault contract. Implementations encrypt at rest.
/// Values are NEVER logged, serialized in events, or returned via API
/// (except one-time rotation responses).
/// </summary>
public interface ISecretsModule
{
    Task SetAsync(SecretKey key, string value, DateTimeOffset? expiresAt = null, CancellationToken ct = default);
    Task<SecretHint?> GetHintAsync(SecretKey key, CancellationToken ct = default);
    Task<string?> GetValueAsync(SecretKey key, CancellationToken ct = default);
    Task DeleteAllAsync(Guid tenantId, string scope, Guid entityId, CancellationToken ct = default);
}
