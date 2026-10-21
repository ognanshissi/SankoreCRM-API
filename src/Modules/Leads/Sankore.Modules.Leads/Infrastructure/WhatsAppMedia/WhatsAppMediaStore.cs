namespace Sankore.Modules.Leads.Infrastructure.WhatsAppMedia;

using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Sankore.Shared.ObjectStorage;

/// <summary>
/// Settings of the WhatsApp media store (WA-BE-20, WA-BE-21), bound from <c>Leads:WhatsApp:Media</c>.
/// Host settings, not tenant parameters — like every other encrypted store: a tenant decides how
/// LONG a media is kept (<c>whatsapp-media-retention-days</c>), never how it is protected.
/// </summary>
public sealed class WhatsAppMediaStorageOptions
{
    public const string SectionName = "Leads:WhatsApp:Media";

    /// <summary>Root of the filesystem fallback. Point it at a mounted volume or configure a bucket in any real environment.</summary>
    public string? BasePath { get; set; }

    /// <summary>
    /// Base64 32-byte MASTER key. It never encrypts a media itself: each object gets its own
    /// random data key, and only that data key is encrypted under this one (see the store).
    /// Distinct from <c>Leads:FieldEncryptionKey</c> — one compromised key opens one concern.
    /// Unset = no store: media are fetched from the provider when opened, as in lot 1.
    /// </summary>
    public string? EncryptionKey { get; set; }

    /// <summary>16 MB — above WhatsApp's own ceilings for images, audio and video (documents go to 100 MB and are refused past this).</summary>
    public long MaxBytes { get; set; } = 16L * 1024 * 1024;
}

public static class WhatsAppMediaStorage
{
    public const string Concern = "whatsapp-media";

    public static string ResolveBasePath(IConfiguration config, IHostEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(config);
        var configured = config[$"{WhatsAppMediaStorageOptions.SectionName}:BasePath"];

        return string.IsNullOrWhiteSpace(configured)
            ? Path.Combine(AppContext.BaseDirectory, Concern)
            : Path.GetFullPath(configured);
    }
}

/// <summary>Where a WhatsApp media's bytes live once fetched or sent.</summary>
internal interface IWhatsAppMediaStore
{
    Task<StoredWhatsAppMedia> StoreAsync(Guid tenantId, byte[] content, CancellationToken ct);

    /// <summary>The bytes, or null when absent or not this tenant's. Throws when the object does not authenticate.</summary>
    Task<byte[]?> OpenAsync(Guid tenantId, string storageRef, CancellationToken ct);

    Task<bool> DeleteAsync(Guid tenantId, string storageRef, CancellationToken ct);
}

/// <param name="Sha256">Of the plaintext, lower-case hex — proves later that the bytes are the ones received.</param>
internal sealed record StoredWhatsAppMedia(string StorageRef, long SizeBytes, string Sha256);

/// <summary>
/// <see cref="IWhatsAppMediaStore"/> over an <see cref="IObjectBackend"/>, with ENVELOPE
/// encryption — which is what the specification asks of media (WA-BE-20), and the one place the
/// solution's other stores do not do it.
///
/// <para>
/// <b>Why an envelope here.</b> Each object is encrypted under its own random 256-bit data key; the
/// data key is encrypted under the master key and travels in the object's header. Rotating the
/// master key therefore re-wraps 32 bytes per object instead of re-encrypting every voice note and
/// photograph, and a data key recovered from memory opens one media, not all of them.
/// </para>
///
/// <para>
/// Object key <c>{tenantToken}/{ab}/{objectId}.wmed</c>; content
/// <c>"WAM1" | wrapNonce(12) | wrapTag(16) | wrappedKey(32) | nonce(12) | tag(16) | ciphertext</c>.
/// The tenant id and the reference are associated data of BOTH encryptions, so an object re-filed
/// under another tenant or another reference does not authenticate. The reference is random and
/// never derived from the message: it must not let anybody enumerate a conversation.
/// </para>
///
/// <para>
/// A fifth copy of the envelope pattern, deliberately not factored with the KYC, batch, claim and
/// recording stores: the format marker and layout are an on-disk contract per concern (see
/// <c>EncryptedRecordingStore</c>).
/// </para>
/// </summary>
internal sealed class EncryptedWhatsAppMediaStore : IWhatsAppMediaStore
{
    private const int NonceSize = 12;
    private const int TagSize = 16;
    private const int KeySize = 32;
    private const string ObjectExtension = ".wmed";

    private static readonly byte[] Magic = "WAM1"u8.ToArray();
    private static readonly int HeaderSize = Magic.Length + NonceSize + TagSize + KeySize + NonceSize + TagSize;

    private static readonly Regex StorageRefShape = new(@"^m1\.[0-9a-f]{16}\.[0-9a-f]{32}\z", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private readonly IObjectBackend _backend;
    private readonly WhatsAppMediaStorageOptions _options;
    private readonly ILogger<EncryptedWhatsAppMediaStore> _logger;
    private readonly byte[] _masterKey;

    public EncryptedWhatsAppMediaStore(
        IObjectBackend backend, IOptions<WhatsAppMediaStorageOptions> options, ILogger<EncryptedWhatsAppMediaStore> logger)
    {
        _backend = backend;
        _options = options.Value;
        _logger = logger;
        _masterKey = ReadKey(_options.EncryptionKey);
    }

    public async Task<StoredWhatsAppMedia> StoreAsync(Guid tenantId, byte[] content, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(content);
        if (content.Length == 0) throw new InvalidOperationException("Refusing to store an empty media.");
        if (content.Length > _options.MaxBytes)
            throw new InvalidOperationException($"A WhatsApp media may not exceed {_options.MaxBytes} bytes.");

        var token = TenantToken(tenantId);
        var storageRef = $"m1.{token}." + Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
        var aad = AssociatedData(tenantId, storageRef);

        var dataKey = RandomNumberGenerator.GetBytes(KeySize);

        try
        {
            var wrapNonce = RandomNumberGenerator.GetBytes(NonceSize);
            var wrapTag = new byte[TagSize];
            var wrappedKey = new byte[KeySize];

            using (var master = new AesGcm(_masterKey, TagSize))
                master.Encrypt(wrapNonce, dataKey, wrappedKey, wrapTag, aad);

            var nonce = RandomNumberGenerator.GetBytes(NonceSize);
            var tag = new byte[TagSize];
            var ciphertext = new byte[content.Length];

            using (var data = new AesGcm(dataKey, TagSize))
                data.Encrypt(nonce, content, ciphertext, tag, aad);

            var stored = new byte[HeaderSize + ciphertext.Length];
            var offset = 0;
            foreach (var part in new[] { Magic, wrapNonce, wrapTag, wrappedKey, nonce, tag, ciphertext })
            {
                part.CopyTo(stored, offset);
                offset += part.Length;
            }

            await _backend.PutAsync(BuildKey(token, storageRef), stored, ct);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(dataKey);
        }

        _logger.LogInformation("Stored a WhatsApp media for tenant {TenantId}: {SizeBytes} bytes", tenantId, content.Length);

        return new StoredWhatsAppMedia(storageRef, content.Length, Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant());
    }

    public async Task<byte[]?> OpenAsync(Guid tenantId, string storageRef, CancellationToken ct)
    {
        if (!TryResolveKey(tenantId, storageRef, out var objectKey)) return null;

        var raw = await _backend.GetAsync(objectKey, _options.MaxBytes + HeaderSize, ct);
        if (raw is null || raw.Length < HeaderSize) return null;

        return Decrypt(raw, tenantId, storageRef);
    }

    public async Task<bool> DeleteAsync(Guid tenantId, string storageRef, CancellationToken ct)
    {
        if (!TryResolveKey(tenantId, storageRef, out var objectKey)) return false;
        return await _backend.DeleteAsync(objectKey, ct);
    }

    private byte[] Decrypt(byte[] raw, Guid tenantId, string storageRef)
    {
        var span = raw.AsSpan();
        if (!span[..Magic.Length].SequenceEqual(Magic))
            throw new CryptographicException("Not a WhatsApp media object (unknown format marker).");

        var offset = Magic.Length;
        ReadOnlySpan<byte> Next(ReadOnlySpan<byte> s, int size, ref int at) { var part = s.Slice(at, size); at += size; return part; }

        var wrapNonce = Next(span, NonceSize, ref offset);
        var wrapTag = Next(span, TagSize, ref offset);
        var wrappedKey = Next(span, KeySize, ref offset);
        var nonce = Next(span, NonceSize, ref offset);
        var tag = Next(span, TagSize, ref offset);
        var ciphertext = span[offset..];

        var aad = AssociatedData(tenantId, storageRef);
        var dataKey = new byte[KeySize];

        try
        {
            using (var master = new AesGcm(_masterKey, TagSize))
                master.Decrypt(wrapNonce, wrappedKey, wrapTag, dataKey, aad);

            var plaintext = new byte[ciphertext.Length];
            using (var data = new AesGcm(dataKey, TagSize))
                data.Decrypt(nonce, ciphertext, tag, plaintext, aad);

            return plaintext;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(dataKey);
        }
    }

    private static bool TryResolveKey(Guid tenantId, string? storageRef, out string objectKey)
    {
        objectKey = string.Empty;
        if (string.IsNullOrEmpty(storageRef) || !StorageRefShape.IsMatch(storageRef)) return false;

        var token = TenantToken(tenantId);
        if (!string.Equals(storageRef.Split('.')[1], token, StringComparison.Ordinal)) return false;

        objectKey = BuildKey(token, storageRef);
        return true;
    }

    private static string BuildKey(string token, string storageRef)
    {
        var objectId = storageRef.Split('.')[2];
        return $"{token}/{objectId[..2]}/{objectId}{ObjectExtension}";
    }

    /// <summary>16 hex of a SHA-256 of the tenant id: stable, and no tenant id in a bucket listing.</summary>
    private static string TenantToken(Guid tenantId) =>
        Convert.ToHexString(SHA256.HashData(tenantId.ToByteArray()))[..16].ToLowerInvariant();

    private static byte[] AssociatedData(Guid tenantId, string storageRef) =>
        Encoding.UTF8.GetBytes($"whatsapp-media:{tenantId:N}:{storageRef}");

    private static byte[] ReadKey(string? base64)
    {
        if (string.IsNullOrWhiteSpace(base64))
            throw new InvalidOperationException($"{WhatsAppMediaStorageOptions.SectionName}:EncryptionKey is not configured.");

        byte[] key;
        try
        {
            key = Convert.FromBase64String(base64);
        }
        catch (FormatException)
        {
            throw new InvalidOperationException($"{WhatsAppMediaStorageOptions.SectionName}:EncryptionKey is not valid base64.");
        }

        if (key.Length != KeySize)
            throw new InvalidOperationException($"{WhatsAppMediaStorageOptions.SectionName}:EncryptionKey must be 32 bytes.");

        return key;
    }
}
