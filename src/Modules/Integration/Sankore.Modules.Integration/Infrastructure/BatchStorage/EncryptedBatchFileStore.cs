namespace Sankore.Modules.Integration.Infrastructure.BatchStorage;

using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Sankore.Shared.Kernel;
using Sankore.Shared.ObjectStorage;

/// <summary>
/// <see cref="IBatchFileStore"/> over any <see cref="IObjectBackend"/>, encrypting every object
/// with AES-256-GCM before it reaches one. Deliberately the same shape as
/// <c>KycDocumentStore</c> — read its comments for the compliance argument; what follows is only
/// what differs here.
///
/// Object key — <c>{tenantToken}/{ab}/{objectId}.ibf</c>, content
/// <c>"IBF1" | nonce(12) | tag(16) | ciphertext</c>:
/// <list type="bullet">
/// <item>the tenant prefix makes a mis-scoped read a missing object rather than a leak, and lets
/// ops drop one tenant's batch history with a single prefix delete on offboarding;</item>
/// <item>the two-character shard keeps a directory listable after a few years of daily files;</item>
/// <item><b>the tenant id AND the reference are fed to GCM as associated data</b>, so a
/// ciphertext written for one tenant cannot be read under another tenant's reference — the tag
/// check fails. That is the property the batch socle needs most: one object here is a whole
/// institution's day of customer writes, so a ciphertext that could be replayed under a second
/// tenant's row would hand that tenant the first one's portfolio.</item>
/// </list>
///
/// <para>
/// The digest is NOT computed here, unlike <c>KycDocumentStore</c>: the checksum of a batch file
/// is an acceptance criterion of its own (criterion 3) and belongs to the row, over the
/// PLAINTEXT, as <c>IntegrationBatchFile</c> explains. A store that hashed what it stored would
/// invite a reader to compare the two and find a checksum of the ciphertext.
/// </para>
/// </summary>
internal sealed class EncryptedBatchFileStore : IBatchFileStore
{
    private const int NonceSize = 12;
    private const int TagSize = 16;
    private const int KeySize = 32;
    private const string ObjectExtension = ".ibf";

    /// <summary>Format marker, so a future layout change is detectable instead of silently garbage.</summary>
    private static readonly byte[] Magic = "IBF1"u8.ToArray();

    private static readonly int HeaderSize = Magic.Length + NonceSize + TagSize;

    /// <summary>
    /// The exact shape <see cref="StoreAsync"/> issues: <c>b1.{16 hex}.{32 hex}</c>. Anything else
    /// is rejected before a key is built — path traversal is handled by never accepting a
    /// caller-supplied name, not by sanitising one. <c>\z</c> rather than <c>$</c> deliberately:
    /// <c>$</c> also matches before a trailing newline.
    /// </summary>
    private static readonly Regex StorageRefShape = new(
        @"^b1\.[0-9a-f]{16}\.[0-9a-f]{32}\z",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private readonly IObjectBackend _backend;
    private readonly IntegrationBatchStorageOptions _options;
    private readonly ILogger<EncryptedBatchFileStore> _logger;
    private readonly byte[] _key;

    public EncryptedBatchFileStore(
        IObjectBackend backend,
        IOptions<IntegrationBatchStorageOptions> options,
        ILogger<EncryptedBatchFileStore> logger)
    {
        ArgumentNullException.ThrowIfNull(options);

        _backend = backend;
        _options = options.Value;
        _logger = logger;

        // Read eagerly, exactly as KycDocumentStore does and for the documented reason: a key read
        // lazily surfaces as "ArgumentNullException (Parameter 's')" out of
        // Convert.FromBase64String in the middle of a job, with nothing in the message naming the
        // setting. This type is resolved from a factory, so "eagerly" means on the first batch
        // generation rather than at boot — a deployment with no batch connection needs no key.
        _key = ReadKey(_options.EncryptionKey);
    }

    public async Task<string> StoreAsync(Guid tenantId, byte[] plaintext, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(plaintext);

        if (plaintext.Length == 0)
            throw new DomainException(
                "INTEGRATION_BATCH_FILE_EMPTY: refusing to store an empty outbound batch file.");

        if (plaintext.Length > _options.MaxBytes)
            throw new DomainException(
                $"INTEGRATION_BATCH_FILE_TOO_LARGE: the generated file is {plaintext.Length} "
                + $"bytes, the limit is {_options.MaxBytes} bytes.");

        // The object id is random and never derived from the connection or the sequence: two
        // references must not reveal that they belong to the same connection, and a sequence
        // embedded in a key would make the whole series enumerable from one leaked reference.
        var storageRef =
            $"b1.{TenantToken(tenantId)}."
            + Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();

        var nonce = RandomNumberGenerator.GetBytes(NonceSize);
        var ciphertext = new byte[plaintext.Length];
        var tag = new byte[TagSize];

        using (var aes = new AesGcm(_key, TagSize))
        {
            aes.Encrypt(nonce, plaintext, ciphertext, tag, AssociatedData(tenantId, storageRef));
        }

        var stored = new byte[HeaderSize + ciphertext.Length];
        Magic.CopyTo(stored, 0);
        nonce.CopyTo(stored, Magic.Length);
        tag.CopyTo(stored, Magic.Length + NonceSize);
        ciphertext.CopyTo(stored, HeaderSize);

        await _backend.PutAsync(BuildKey(TenantToken(tenantId), storageRef), stored, ct);

        _logger.LogInformation(
            "Stored outbound batch object for tenant {TenantId}: {SizeBytes} plaintext bytes",
            tenantId, plaintext.Length);

        return storageRef;
    }

    public async Task<byte[]?> OpenAsync(Guid tenantId, string storageRef, CancellationToken ct)
    {
        if (!TryResolveKey(tenantId, storageRef, out var objectKey))
        {
            // Debug, not Warning: a reference belonging to another tenant is ordinary traffic for
            // a job that walks tenants, and a genuine probe shows up as a burst at the API layer.
            _logger.LogDebug("Rejected batch storage ref for tenant {TenantId}", tenantId);
            return null;
        }

        // The ceiling is handed to the backend rather than checked on the way back: it decides
        // before the bytes are in memory, and answers null if the medium holds something bigger
        // than this store could ever have written.
        var raw = await _backend.GetAsync(objectKey, _options.MaxBytes + HeaderSize, ct);
        if (raw is null) return null;

        if (raw.Length < HeaderSize)
        {
            _logger.LogWarning(
                "Batch object {StorageRef} is {Length} bytes, shorter than its own header, ignoring",
                storageRef, raw.Length);
            return null;
        }

        return Decrypt(raw, tenantId, storageRef);
    }

    public async Task<bool> DeleteAsync(Guid tenantId, string storageRef, CancellationToken ct)
    {
        if (!TryResolveKey(tenantId, storageRef, out var objectKey)) return false;

        var removed = await _backend.DeleteAsync(objectKey, ct);

        if (removed)
            _logger.LogInformation(
                "Purged batch object {StorageRef} for tenant {TenantId}", storageRef, tenantId);

        return removed;
    }

    /// <summary>
    /// Kept out of the async method on purpose: the spans below must not be hoisted into a state
    /// machine, and the decrypt is pure CPU work on a buffer already in memory.
    /// </summary>
    private byte[] Decrypt(byte[] raw, Guid tenantId, string storageRef)
    {
        if (!raw.AsSpan(0, Magic.Length).SequenceEqual(Magic))
            throw IntegrityFailure(storageRef, "unexpected format marker");

        var nonce = raw.AsSpan(Magic.Length, NonceSize);
        var tag = raw.AsSpan(Magic.Length + NonceSize, TagSize);
        var ciphertext = raw.AsSpan(HeaderSize);
        var plaintext = new byte[ciphertext.Length];

        try
        {
            using var aes = new AesGcm(_key, TagSize);
            aes.Decrypt(nonce, ciphertext, tag, plaintext, AssociatedData(tenantId, storageRef));
        }
        catch (CryptographicException ex)
        {
            // Never degraded to "not found": the object exists and does not authenticate, which
            // means a swapped file, a bit-rotted volume or the wrong key. All three are incidents
            // a compliance officer has to hear about, not empty results — and for a batch file the
            // alternative would be re-depositing a file we can no longer vouch for.
            throw IntegrityFailure(storageRef, "authentication tag mismatch", ex);
        }

        return plaintext;
    }

    private static bool TryResolveKey(Guid tenantId, string storageRef, out string objectKey)
    {
        objectKey = string.Empty;
        if (string.IsNullOrEmpty(storageRef) || !StorageRefShape.IsMatch(storageRef)) return false;

        var token = TenantToken(tenantId);

        // The tenant segment is derived from the tenant id, so a reference issued for another
        // tenant simply does not match — no lookup table, nothing to forget to filter on.
        if (!string.Equals(storageRef.Split('.')[1], token, StringComparison.Ordinal)) return false;

        objectKey = BuildKey(token, storageRef);
        return true;
    }

    /// <summary>
    /// <c>/</c> on every platform, never <see cref="Path.DirectorySeparatorChar"/>: this is a key,
    /// and a backend that happens to be a filesystem is what translates it. Building it with the
    /// platform separator would give a Windows host different keys from a Linux one.
    /// </summary>
    private static string BuildKey(string tenantToken, string storageRef)
    {
        var objectId = storageRef.Split('.')[2];
        return $"{tenantToken}/{objectId[..2]}/{objectId}{ObjectExtension}";
    }

    /// <summary>
    /// Stable per-tenant prefix — a hash rather than the raw GUID, so a reference that reaches a
    /// log or an export carries no tenant identifier to correlate on.
    /// </summary>
    private static string TenantToken(Guid tenantId)
        => Convert.ToHexString(SHA256.HashData(tenantId.ToByteArray()))[..16].ToLowerInvariant();

    /// <summary>
    /// GCM associated data: authenticated, not encrypted. Binds the ciphertext to the tenant and
    /// to its own reference, so relocating or re-filing an object cannot make it decrypt
    /// elsewhere.
    /// </summary>
    private static byte[] AssociatedData(Guid tenantId, string storageRef)
        => Encoding.UTF8.GetBytes($"ibf1|{tenantId:N}|{storageRef}");

    private DomainException IntegrityFailure(string storageRef, string reason, Exception? inner = null)
    {
        _logger.LogError(inner, "Batch object {StorageRef} failed integrity check: {Reason}", storageRef, reason);
        return new DomainException(
            $"INTEGRATION_BATCH_FILE_INTEGRITY_FAILURE: stored batch file '{storageRef}' could "
            + $"not be authenticated ({reason}).");
    }

    private static byte[] ReadKey(string? configured)
    {
        const string setting = $"{IntegrationBatchStorageOptions.SectionName}:EncryptionKey";
        const string guidance =
            "Generate one with: Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))";

        if (string.IsNullOrWhiteSpace(configured))
            throw new InvalidOperationException(
                $"{setting} is not configured (Base64, {KeySize} bytes). {guidance}");

        byte[] key;
        try
        {
            key = Convert.FromBase64String(configured);
        }
        catch (FormatException ex)
        {
            throw new InvalidOperationException($"{setting} is not valid Base64. {guidance}", ex);
        }

        if (key.Length != KeySize)
            throw new InvalidOperationException(
                $"{setting} must be exactly {KeySize} bytes once Base64-decoded (AES-256), but "
                + $"{key.Length} were provided. {guidance}");

        return key;
    }
}
