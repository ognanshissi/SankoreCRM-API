namespace Sankore.Shared.Infrastructure.Secrets;

/// <summary>
/// Encrypted secret stored in the vault table.
/// Value is AES-256-GCM encrypted; IV and tag are stored alongside.
/// </summary>
public sealed class SecretEntry
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public string Scope { get; set; } = default!;
    public Guid EntityId { get; set; }
    public string Name { get; set; } = default!;

    /// <summary>Base64-encoded ciphertext (AES-256-GCM).</summary>
    public string EncryptedValue { get; set; } = default!;

    /// <summary>Base64-encoded IV (12 bytes for GCM).</summary>
    public string Iv { get; set; } = default!;

    /// <summary>Base64-encoded authentication tag (16 bytes for GCM).</summary>
    public string Tag { get; set; } = default!;

    public DateTimeOffset? ExpiresAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}
