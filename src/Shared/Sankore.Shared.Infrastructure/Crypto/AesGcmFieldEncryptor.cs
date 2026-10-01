namespace Sankore.Shared.Infrastructure.Crypto;

using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;

/// <summary>
/// AES-256-GCM implementation of <see cref="IFieldEncryptor"/>, modelled on
/// <see cref="Secrets.AesSecretsModule"/> but producing a single opaque string
/// (so a sensitive column needs one column, not three).
///
/// A fresh 12-byte nonce is drawn for every call, therefore encrypting the same
/// plaintext twice yields two different payloads — sensitive columns are NOT
/// searchable, that is what the blind indexes (<see cref="IBlindIndexer"/>) are
/// for. The 16-byte GCM tag authenticates the payload: any tampering makes
/// <see cref="Decrypt"/> throw instead of returning garbage.
/// </summary>
public sealed class AesGcmFieldEncryptor : IFieldEncryptor
{
    private const string Version = "v1";
    private const int NonceSize = 12;
    private const int TagSize = 16;
    private const int KeySize = 32;

    private readonly Lazy<byte[]> _key;

    public AesGcmFieldEncryptor(IOptions<FieldProtectionOptions> options)
    {
        // Lazy so a misconfigured key surfaces as a clear error on first use
        // rather than while the DI container is being built.
        _key = new Lazy<byte[]>(() => ReadKey(options.Value.FieldEncryptionKey, options.Value.SectionName));
    }

    public string? Encrypt(string? plaintext)
    {
        if (string.IsNullOrWhiteSpace(plaintext)) return null;

        var nonce = RandomNumberGenerator.GetBytes(NonceSize);
        var plainBytes = Encoding.UTF8.GetBytes(plaintext);
        var cipherBytes = new byte[plainBytes.Length];
        var tag = new byte[TagSize];

        using var aes = new AesGcm(_key.Value, TagSize);
        aes.Encrypt(nonce, plainBytes, cipherBytes, tag);

        return string.Concat(
            Version, ":",
            Convert.ToBase64String(nonce), ":",
            Convert.ToBase64String(tag), ":",
            Convert.ToBase64String(cipherBytes));
    }

    public string? Decrypt(string? ciphertext)
    {
        if (string.IsNullOrWhiteSpace(ciphertext)) return null;

        var parts = ciphertext.Split(':');
        if (parts.Length != 4)
            throw new InvalidOperationException(
                "Malformed protected field payload: expected 'v1:nonce:tag:ciphertext'.");

        if (!string.Equals(parts[0], Version, StringComparison.Ordinal))
            throw new InvalidOperationException(
                $"Unsupported protected field payload version '{parts[0]}'. Expected '{Version}'.");

        byte[] nonce, tag, cipherBytes;
        try
        {
            nonce = Convert.FromBase64String(parts[1]);
            tag = Convert.FromBase64String(parts[2]);
            cipherBytes = Convert.FromBase64String(parts[3]);
        }
        catch (FormatException ex)
        {
            throw new InvalidOperationException(
                "Malformed protected field payload: nonce, tag and ciphertext must be Base64.", ex);
        }

        if (nonce.Length != NonceSize || tag.Length != TagSize)
            throw new InvalidOperationException(
                $"Malformed protected field payload: nonce must be {NonceSize} bytes and tag {TagSize} bytes.");

        var plainBytes = new byte[cipherBytes.Length];

        using var aes = new AesGcm(_key.Value, TagSize);
        aes.Decrypt(nonce, cipherBytes, tag, plainBytes);

        return Encoding.UTF8.GetString(plainBytes);
    }

    private static byte[] ReadKey(string? configured, string section)
    {
        const string guidance =
            "Generate one with: Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))";

        if (string.IsNullOrWhiteSpace(configured))
            throw new InvalidOperationException(
                $"{section}:FieldEncryptionKey is not configured (Base64, {KeySize} bytes). {guidance}");

        byte[] key;
        try
        {
            key = Convert.FromBase64String(configured);
        }
        catch (FormatException ex)
        {
            throw new InvalidOperationException(
                $"{section}:FieldEncryptionKey is not valid Base64. {guidance}", ex);
        }

        if (key.Length != KeySize)
            throw new InvalidOperationException(
                $"{section}:FieldEncryptionKey must be exactly {KeySize} bytes once Base64-decoded "
                + $"(AES-256), but {key.Length} were provided. {guidance}");

        return key;
    }
}
