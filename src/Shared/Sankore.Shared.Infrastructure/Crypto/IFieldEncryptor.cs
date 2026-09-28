namespace Sankore.Shared.Infrastructure.Crypto;

/// <summary>
/// Reversible column-level protection for personally identifiable data
/// (document numbers, dates of birth, incomes, phone numbers, addresses...).
///
/// The payload format is self-describing so the key/algorithm can be rotated
/// later without a data migration: <c>"v1:" + base64(nonce) + ":" + base64(tag)
/// + ":" + base64(ciphertext)</c>.
/// </summary>
public interface IFieldEncryptor
{
    /// <summary>AES-256-GCM. Returns <c>"v1:nonce:tag:ciphertext"</c> (all Base64).</summary>
    string? Encrypt(string? plaintext);

    /// <summary>Reverses <see cref="Encrypt"/>. null-in → null-out.</summary>
    string? Decrypt(string? ciphertext);
}
