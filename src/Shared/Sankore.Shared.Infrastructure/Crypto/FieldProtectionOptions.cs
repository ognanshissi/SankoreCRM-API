namespace Sankore.Shared.Infrastructure.Crypto;

/// <summary>
/// Keys protecting the sensitive columns of a module (PII encryption + blind
/// indexes). Bound from a configuration section by the caller — the Customers
/// module binds <c>"Customers"</c>, so the settings are
/// <c>Customers:FieldEncryptionKey</c> and <c>Customers:BlindIndexKey</c>.
///
/// Both values are Base64-encoded. Generate them with:
/// <c>Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))</c>.
/// </summary>
public sealed class FieldProtectionOptions
{
    /// <summary>Base64-encoded 32-byte AES-256-GCM key.</summary>
    public string FieldEncryptionKey { get; set; } = default!;

    /// <summary>Base64-encoded HMAC-SHA256 key (32 bytes or more).</summary>
    public string BlindIndexKey { get; set; } = default!;
}
