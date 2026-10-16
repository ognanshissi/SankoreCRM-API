namespace Sankore.Modules.Integration.Infrastructure.RecordingStorage;

using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Sankore.Shared.Kernel;
using Sankore.Shared.ObjectStorage;

/// <summary>
/// <see cref="IRecordingStore"/> over any <see cref="IObjectBackend"/>, encrypting every object
/// with AES-256-GCM before it reaches one (TEL-B-16).
///
/// <para>
/// <b>This is the FOURTH copy of this envelope in the solution</b> — <c>KycDocumentStore</c>,
/// <c>EncryptedBatchFileStore</c> and <c>EncryptedClaimDocumentStore</c> are the others — and the
/// duplication is deliberate, for the reason the claim store states at length: each one's format
/// marker, reference shape and object extension are an ON-DISK contract, so factoring them into a
/// shared primitive would make a change to one a change to the bytes already written by the other
/// three. The failure mode of that mistake here is a recording that no longer authenticates,
/// which is evidence lost. The cost is four similar files; the alternative is one file whose every
/// edit migrates four keyspaces.
/// </para>
///
/// Object key — <c>{tenantToken}/{ab}/{objectId}.trec</c>, content
/// <c>"TRC1" | nonce(12) | tag(16) | ciphertext</c>:
/// <list type="bullet">
/// <item>the tenant prefix makes a mis-scoped read a missing object rather than a leak, and lets
/// ops drop one tenant's recordings with a single prefix delete on offboarding;</item>
/// <item>the two-character shard keeps a directory listable after years of calls — and this is the
/// highest-volume concern of the four, since a busy agency produces hundreds a day;</item>
/// <item><b>the tenant id AND the reference are fed to GCM as associated data</b>, so a ciphertext
/// written for one tenant cannot be read under another tenant's reference: the tag check fails.
/// Re-filing an object cannot make it decrypt elsewhere.</item>
/// </list>
///
/// <para>
/// <b>Why the reference is random and never derived from the call.</b> It appears in the player's
/// URL, so two references must not reveal that they belong to the same customer — and a reference
/// built from a call id would let anybody holding one enumerate a conversation's neighbours.
/// </para>
/// </summary>
internal sealed class EncryptedRecordingStore : IRecordingStore
{
    private const int NonceSize = 12;
    private const int TagSize = 16;
    private const int KeySize = 32;
    private const string ObjectExtension = ".trec";

    /// <summary>Format marker, so a future layout change is detectable instead of silently garbage.</summary>
    private static readonly byte[] Magic = "TRC1"u8.ToArray();

    private static readonly int HeaderSize = Magic.Length + NonceSize + TagSize;

    /// <summary>
    /// The exact shape <see cref="StoreAsync"/> issues: <c>r1.{16 hex}.{32 hex}</c>. Anything else
    /// is rejected before a key is built — path traversal is handled by never accepting a
    /// caller-supplied name, not by sanitising one. <c>\z</c> rather than <c>$</c> deliberately:
    /// <c>$</c> also matches before a trailing newline.
    /// </summary>
    private static readonly Regex StorageRefShape = new(
        @"^r1\.[0-9a-f]{16}\.[0-9a-f]{32}\z",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private readonly IObjectBackend _backend;
    private readonly RecordingStorageOptions _options;
    private readonly ILogger<EncryptedRecordingStore> _logger;
    private readonly byte[] _key;

    public EncryptedRecordingStore(
        IObjectBackend backend,
        IOptions<RecordingStorageOptions> options,
        ILogger<EncryptedRecordingStore> logger)
    {
        ArgumentNullException.ThrowIfNull(options);

        _backend = backend;
        _options = options.Value;
        _logger = logger;

        // Read eagerly, as all three sibling stores do and for the documented reason: a key read
        // lazily surfaces as "ArgumentNullException (Parameter 's')" out of
        // Convert.FromBase64String in the middle of a retrieval, with nothing in the message
        // naming the setting. This type is resolved from a factory, so "eagerly" means on the
        // first retrieval rather than at boot — and since the recording policy ships Disabled,
        // a deployment that never switches it on needs no key at all.
        _key = ReadKey(_options.EncryptionKey);
    }

    public async Task<StoredRecording> StoreAsync(
        Guid tenantId, Guid callId, byte[] content, string contentType, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(content);

        var normalized = NormalizeContentType(contentType);

        if (!_options.AllowedContentTypes.Contains(normalized, StringComparer.OrdinalIgnoreCase))
            throw new DomainException(
                $"{RecordingStoreErrors.ContentTypeNotAllowed}: '{contentType}' is not an accepted "
                + $"recording format ({string.Join(", ", _options.AllowedContentTypes)}).");

        if (content.Length == 0)
            throw new DomainException(
                $"{RecordingStoreErrors.Empty}: refusing to store an empty recording.");

        if (content.Length > _options.MaxBytes)
        {
            throw new DomainException(
                $"{RecordingStoreErrors.TooLarge}: a recording may not exceed "
                + $"{_options.MaxBytes} bytes.");
        }

        var storageRef =
            $"r1.{TenantToken(tenantId)}."
            + Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();

        var nonce = RandomNumberGenerator.GetBytes(NonceSize);
        var ciphertext = new byte[content.Length];
        var tag = new byte[TagSize];

        using (var aes = new AesGcm(_key, TagSize))
        {
            aes.Encrypt(nonce, content, ciphertext, tag, AssociatedData(tenantId, storageRef));
        }

        var stored = new byte[HeaderSize + ciphertext.Length];
        Magic.CopyTo(stored, 0);
        nonce.CopyTo(stored, Magic.Length);
        tag.CopyTo(stored, Magic.Length + NonceSize);
        ciphertext.CopyTo(stored, HeaderSize);

        await _backend.PutAsync(BuildKey(TenantToken(tenantId), storageRef), stored, ct);

        var sha256 = Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant();

        // The call id and the size, and nothing of the conversation. There is no file name to
        // withhold here — a provider gives bytes — but the rule is the same as the KYC and claim
        // uploads': a log travels further than the screen.
        _logger.LogInformation(
            "Stored the recording of call {CallId} (tenant {TenantId}): {SizeBytes} bytes, "
            + "{ContentType}", callId, tenantId, content.Length, normalized);

        return new StoredRecording(storageRef, normalized, content.Length, sha256);
    }

    public async Task<byte[]?> OpenAsync(Guid tenantId, string storageRef, CancellationToken ct)
    {
        if (!TryResolveKey(tenantId, storageRef, out var objectKey))
        {
            // Debug, not Warning: a reference belonging to another tenant is ordinary traffic for
            // the purge job that walks tenants, and a genuine probe shows up as a burst at the
            // API layer, where the rate limiter is.
            _logger.LogDebug("Rejected a recording storage ref for tenant {TenantId}", tenantId);

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
                "Recording object {StorageRef} is {Length} bytes, shorter than its own header, "
                + "ignoring", storageRef, raw.Length);

            return null;
        }

        return Decrypt(raw, tenantId, storageRef);
    }

    public async Task<bool> DeleteAsync(Guid tenantId, string storageRef, CancellationToken ct)
    {
        if (!TryResolveKey(tenantId, storageRef, out var objectKey)) return false;

        var removed = await _backend.DeleteAsync(objectKey, ct);

        if (removed)
        {
            _logger.LogInformation(
                "Removed recording object {StorageRef} for tenant {TenantId}",
                storageRef, tenantId);
        }

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
            // means a swapped file, a bit-rotted volume or the wrong key. Answering "no recording"
            // would have an institution tell a customer, or a regulator, that a conversation was
            // never recorded when a file for it is sitting on the volume.
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
    /// log or a URL carries no tenant identifier to correlate on.
    /// </summary>
    private static string TenantToken(Guid tenantId)
        => Convert.ToHexString(SHA256.HashData(tenantId.ToByteArray()))[..16].ToLowerInvariant();

    /// <summary>
    /// GCM associated data: authenticated, not encrypted. Binds the ciphertext to the tenant and
    /// to its own reference, so relocating or re-filing an object cannot make it decrypt elsewhere.
    /// </summary>
    private static byte[] AssociatedData(Guid tenantId, string storageRef)
        => Encoding.UTF8.GetBytes($"trc1|{tenantId:N}|{storageRef}");

    /// <summary>
    /// Parameters stripped and lower-cased: a provider sends <c>audio/mpeg; rate=8000</c> often
    /// enough that comparing the raw header against an allow-list refuses honest files.
    /// </summary>
    private static string NormalizeContentType(string? contentType)
    {
        if (string.IsNullOrWhiteSpace(contentType)) return string.Empty;

        var separator = contentType.IndexOf(';', StringComparison.Ordinal);

        return (separator < 0 ? contentType : contentType[..separator])
            .Trim()
            .ToLowerInvariant();
    }

    private DomainException IntegrityFailure(
        string storageRef, string reason, Exception? inner = null)
    {
        _logger.LogError(
            inner, "Recording object {StorageRef} failed its integrity check: {Reason}",
            storageRef, reason);

        return new DomainException(
            $"{RecordingStoreErrors.IntegrityFailure}: stored recording '{storageRef}' could not "
            + $"be authenticated ({reason}).");
    }

    private static byte[] ReadKey(string? configured)
    {
        const string setting = $"{RecordingStorageOptions.SectionName}:EncryptionKey";
        const string guidance =
            "Generate one with: Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))";

        if (string.IsNullOrWhiteSpace(configured))
        {
            throw new InvalidOperationException(
                $"{setting} is not configured (Base64, {KeySize} bytes). It is only needed by a "
                + $"deployment whose tenants switch call recording on. {guidance}");
        }

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
        {
            throw new InvalidOperationException(
                $"{setting} must be exactly {KeySize} bytes once Base64-decoded (AES-256), but "
                + $"{key.Length} were provided. {guidance}");
        }

        return key;
    }
}

/// <summary>Stable codes the recording store answers with.</summary>
internal static class RecordingStoreErrors
{
    public const string Empty = "TELEPHONY_RECORDING_EMPTY";

    public const string TooLarge = "TELEPHONY_RECORDING_TOO_LARGE";

    public const string ContentTypeNotAllowed = "TELEPHONY_RECORDING_CONTENT_TYPE_NOT_ALLOWED";

    public const string IntegrityFailure = "TELEPHONY_RECORDING_INTEGRITY_FAILURE";
}
