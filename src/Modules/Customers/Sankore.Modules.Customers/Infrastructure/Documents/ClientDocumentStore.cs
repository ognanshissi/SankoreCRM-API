namespace Sankore.Modules.Customers.Infrastructure.Documents;

using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Sankore.Shared.ObjectStorage;

/// <summary>Section <c>Customers:Documents</c>.</summary>
public sealed class ClientDocumentStorageOptions
{
    public const string SectionName = "Customers:Documents";

    /// <summary>Local fallback folder when no bucket is configured. Default: under the content root.</summary>
    public string? BasePath { get; set; }

    /// <summary>Master key (base64, 32 bytes). Unset = no store registered: uploads answer « indisponible ».</summary>
    public string? EncryptionKey { get; set; }

    public long MaxBytes { get; set; } = 10L * 1024 * 1024;
}

public static class ClientDocumentStorage
{
    public const string Concern = "client-documents";

    public static string ResolveBasePath(IConfiguration config, IHostEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(config);
        var configured = config[$"{ClientDocumentStorageOptions.SectionName}:BasePath"];

        return string.IsNullOrWhiteSpace(configured)
            ? Path.Combine(AppContext.BaseDirectory, Concern)
            : Path.GetFullPath(configured);
    }
}

internal interface IClientDocumentStore
{
    Task<StoredClientDocument> StoreAsync(Guid tenantId, byte[] content, CancellationToken ct);

    Task<byte[]?> OpenAsync(Guid tenantId, string storageRef, CancellationToken ct);

    Task<bool> DeleteAsync(Guid tenantId, string storageRef, CancellationToken ct);
}

internal sealed record StoredClientDocument(string StorageRef, long SizeBytes, string Sha256);

/// <summary>
/// The files an agent files on a client record (2026-10-26), envelope-encrypted: a random data key
/// per file, wrapped by the master key, tenant and reference bound as associated data of both. Its
/// OWN key and its own concern — one compromised key opens one kind of content. Copied from the
/// task-attachment store rather than shared, as every store of this solution is: the format marker
/// and the associated data are what keep two concerns' objects apart.
/// </summary>
internal sealed class EncryptedClientDocumentStore : IClientDocumentStore
{
    private const int NonceSize = 12;
    private const int TagSize = 16;
    private const int KeySize = 32;
    private const string ObjectExtension = ".cdoc";

    private static readonly byte[] Magic = "CDO1"u8.ToArray();
    private static readonly int HeaderSize = Magic.Length + NonceSize + TagSize + KeySize + NonceSize + TagSize;

    private static readonly Regex StorageRefShape = new(@"^c1\.[0-9a-f]{16}\.[0-9a-f]{32}\z", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private readonly IObjectBackend _backend;
    private readonly ClientDocumentStorageOptions _options;
    private readonly ILogger<EncryptedClientDocumentStore> _logger;
    private readonly byte[] _masterKey;

    public EncryptedClientDocumentStore(
        IObjectBackend backend, IOptions<ClientDocumentStorageOptions> options, ILogger<EncryptedClientDocumentStore> logger)
    {
        _backend = backend;
        _options = options.Value;
        _logger = logger;
        _masterKey = ReadKey(_options.EncryptionKey);
    }

    public async Task<StoredClientDocument> StoreAsync(Guid tenantId, byte[] content, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(content);
        if (content.Length == 0) throw new InvalidOperationException("Refusing to store an empty file.");
        if (content.Length > _options.MaxBytes)
            throw new InvalidOperationException($"A client document may not exceed {_options.MaxBytes} bytes.");

        var token = TenantToken(tenantId);
        var storageRef = $"c1.{token}." + Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
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

        _logger.LogInformation("Stored a client document for tenant {TenantId}: {SizeBytes} bytes", tenantId, content.Length);
        return new StoredClientDocument(storageRef, content.Length, Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant());
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
            throw new CryptographicException("Not a client document object (unknown format marker).");

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

        // A reference minted for another tenant is refused before anything is read.
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

    private static string TenantToken(Guid tenantId) =>
        Convert.ToHexString(SHA256.HashData(tenantId.ToByteArray()))[..16].ToLowerInvariant();

    private static byte[] AssociatedData(Guid tenantId, string storageRef) =>
        Encoding.UTF8.GetBytes($"client-document:{tenantId:N}:{storageRef}");

    private static byte[] ReadKey(string? base64)
    {
        if (string.IsNullOrWhiteSpace(base64))
            throw new InvalidOperationException($"{ClientDocumentStorageOptions.SectionName}:EncryptionKey is not configured.");

        byte[] key;
        try
        {
            key = Convert.FromBase64String(base64);
        }
        catch (FormatException)
        {
            throw new InvalidOperationException($"{ClientDocumentStorageOptions.SectionName}:EncryptionKey is not valid base64.");
        }

        if (key.Length != KeySize)
            throw new InvalidOperationException($"{ClientDocumentStorageOptions.SectionName}:EncryptionKey must be 32 bytes.");

        return key;
    }
}

/// <summary>
/// What a client document may be, read from its first bytes — never from what the browser
/// declared. Copied from <c>TaskAttachmentTypes</c>: PDF, JPEG, PNG, Office (the extension must
/// agree with the container), plain text and CSV. Anything else — an executable, an HTML page —
/// is refused.
/// </summary>
internal static class ClientDocumentTypes
{
    private static readonly Dictionary<string, string> Ooxml = new(StringComparer.OrdinalIgnoreCase)
    {
        [".docx"] = "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
        [".xlsx"] = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
        [".pptx"] = "application/vnd.openxmlformats-officedocument.presentationml.presentation",
    };

    private static readonly Dictionary<string, string> Ole = new(StringComparer.OrdinalIgnoreCase)
    {
        [".doc"] = "application/msword",
        [".xls"] = "application/vnd.ms-excel",
    };

    public static string? Detect(byte[] content, string? fileName)
    {
        var ext = Path.GetExtension(fileName ?? string.Empty);
        ReadOnlySpan<byte> head = content.AsSpan(0, Math.Min(content.Length, 8));

        if (head.StartsWith("%PDF"u8)) return "application/pdf";
        if (head.StartsWith(new byte[] { 0xFF, 0xD8, 0xFF })) return "image/jpeg";
        if (head.StartsWith(new byte[] { 0x89, 0x50, 0x4E, 0x47 })) return "image/png";
        if (head.StartsWith("PK\u0003\u0004"u8)) return Ooxml.GetValueOrDefault(ext);
        if (head.StartsWith(new byte[] { 0xD0, 0xCF, 0x11, 0xE0 })) return Ole.GetValueOrDefault(ext);

        if (ext.Equals(".txt", StringComparison.OrdinalIgnoreCase) || ext.Equals(".csv", StringComparison.OrdinalIgnoreCase))
        {
            var sample = content.AsSpan(0, Math.Min(content.Length, 8192));
            if (sample.Contains((byte)0)) return null;
            return ext.Equals(".csv", StringComparison.OrdinalIgnoreCase) ? "text/csv" : "text/plain";
        }

        return null;
    }
}
