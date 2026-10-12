namespace Sankore.Modules.Integration.Infrastructure.ClaimStorage;

using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Sankore.Shared.Kernel;
using Sankore.Shared.ObjectStorage;

/// <summary>
/// <see cref="IClaimDocumentStore"/> over any <see cref="IObjectBackend"/>, encrypting every
/// object with AES-256-GCM before it reaches one.
///
/// <para>
/// <b>This is the THIRD copy of this envelope in the solution</b> — <c>KycDocumentStore</c> and
/// <c>EncryptedBatchFileStore</c> are the others — and the duplication is deliberate rather than
/// overlooked. Each one's format marker, reference shape and object extension are an ON-DISK
/// contract: factoring them into a shared primitive would make a change to one a change to the
/// bytes already written by the other two, and the failure mode of that mistake is evidence that
/// no longer authenticates. The cost is three similar files; the alternative is one file whose
/// every edit is a migration of three keyspaces.
/// </para>
///
/// Object key — <c>{tenantToken}/{ab}/{objectId}.icd</c>, content
/// <c>"ICD1" | nonce(12) | tag(16) | ciphertext</c>:
/// <list type="bullet">
/// <item>the tenant prefix makes a mis-scoped read a missing object rather than a leak, and lets
/// ops drop one tenant's claim pieces with a single prefix delete on offboarding;</item>
/// <item>the two-character shard keeps a directory listable after years of dossiers;</item>
/// <item><b>the tenant id AND the reference are fed to GCM as associated data</b>, so a ciphertext
/// written for one tenant cannot be read under another tenant's reference — the tag check fails.
/// Re-filing an object cannot make it decrypt elsewhere.</item>
/// </list>
///
/// <para>
/// The digest IS computed here, unlike <c>EncryptedBatchFileStore</c>: it goes into
/// <c>ux_ins_claim_document_digest</c>, so the row and the object cannot disagree about which file
/// was stored — which is the whole basis of refusing the same piece twice.
/// </para>
/// </summary>
internal sealed class EncryptedClaimDocumentStore : IClaimDocumentStore
{
    private const int NonceSize = 12;
    private const int TagSize = 16;
    private const int KeySize = 32;
    private const string ObjectExtension = ".icd";

    /// <summary>Format marker, so a future layout change is detectable instead of silently garbage.</summary>
    private static readonly byte[] Magic = "ICD1"u8.ToArray();

    private static readonly int HeaderSize = Magic.Length + NonceSize + TagSize;

    /// <summary>
    /// The exact shape <see cref="StoreAsync"/> issues: <c>c1.{16 hex}.{32 hex}</c>. Anything else
    /// is rejected before a key is built — path traversal is handled by never accepting a
    /// caller-supplied name, not by sanitising one. <c>\z</c> rather than <c>$</c> deliberately:
    /// <c>$</c> also matches before a trailing newline.
    /// </summary>
    private static readonly Regex StorageRefShape = new(
        @"^c1\.[0-9a-f]{16}\.[0-9a-f]{32}\z",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private readonly IObjectBackend _backend;
    private readonly ClaimStorageOptions _options;
    private readonly ILogger<EncryptedClaimDocumentStore> _logger;
    private readonly byte[] _key;

    public EncryptedClaimDocumentStore(
        IObjectBackend backend,
        IOptions<ClaimStorageOptions> options,
        ILogger<EncryptedClaimDocumentStore> logger)
    {
        ArgumentNullException.ThrowIfNull(options);

        _backend = backend;
        _options = options.Value;
        _logger = logger;

        // Read eagerly, as both sibling stores do and for the documented reason: a key read lazily
        // surfaces as "ArgumentNullException (Parameter 's')" out of Convert.FromBase64String in
        // the middle of an upload, with nothing in the message naming the setting. This type is
        // resolved from a factory, so "eagerly" means on the first upload rather than at boot — a
        // deployment that distributes no insurance needs no key.
        _key = ReadKey(_options.EncryptionKey);
    }

    public async Task<StoredClaimDocument> StoreAsync(
        Guid tenantId, Guid claimId, Stream content, string contentType, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(content);

        var normalized = NormalizeContentType(contentType);

        if (!_options.AllowedContentTypes.Contains(normalized, StringComparer.OrdinalIgnoreCase))
            throw new DomainException(
                $"{ClaimDocumentErrors.ContentTypeNotAllowed}: '{contentType}' is not an accepted "
                + $"claim piece ({string.Join(", ", _options.AllowedContentTypes)}).");

        var plaintext = await ReadBoundedAsync(content, ct);

        // The object id is random and never derived from the claim: the reference ends up in URLs,
        // and two references must not reveal that they belong to the same dossier.
        var storageRef =
            $"c1.{TenantToken(tenantId)}."
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

        var sha256 = Convert.ToHexString(SHA256.HashData(plaintext)).ToLowerInvariant();

        // The FILE NAME is not logged: an agent names a scan after the customer often enough that
        // it is personal data, and logs travel further than the claims screen. Same rule the KYC
        // upload follows.
        _logger.LogInformation(
            "Stored claim piece for dossier {ClaimId} (tenant {TenantId}): {SizeBytes} bytes, "
            + "{ContentType}",
            claimId, tenantId, plaintext.Length, normalized);

        return new StoredClaimDocument(storageRef, normalized, plaintext.Length, sha256);
    }

    public async Task<byte[]?> OpenAsync(Guid tenantId, string storageRef, CancellationToken ct)
    {
        if (!TryResolveKey(tenantId, storageRef, out var objectKey))
        {
            // Debug, not Warning: a reference belonging to another tenant is ordinary traffic for a
            // job that walks tenants, and a genuine probe shows up as a burst at the API layer.
            _logger.LogDebug("Rejected claim storage ref for tenant {TenantId}", tenantId);
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
                "Claim object {StorageRef} is {Length} bytes, shorter than its own header, ignoring",
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
        {
            _logger.LogInformation(
                "Removed claim object {StorageRef} for tenant {TenantId}", storageRef, tenantId);
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
            // means a swapped file, a bit-rotted volume or the wrong key. For a pièce de sinistre
            // the alternative would be transmitting to an insurer a document we can no longer
            // vouch for, or quietly dropping evidence from a dossier.
            throw IntegrityFailure(storageRef, "authentication tag mismatch", ex);
        }

        return plaintext;
    }

    private async Task<byte[]> ReadBoundedAsync(Stream content, CancellationToken ct)
    {
        // Copied with a ceiling rather than read whole: Length is unavailable on a chunked upload
        // and a declared Content-Length is the client's claim, not a fact.
        using var buffer = new MemoryStream();

        var limit = _options.MaxBytes;
        var pool = new byte[81920];
        long total = 0;

        while (true)
        {
            var read = await content.ReadAsync(pool, ct);
            if (read == 0) break;

            total += read;

            if (total > limit)
                throw new DomainException(
                    $"{ClaimDocumentErrors.TooLarge}: a claim piece may not exceed {limit} bytes.");

            await buffer.WriteAsync(pool.AsMemory(0, read), ct);
        }

        if (total == 0)
            throw new DomainException(
                $"{ClaimDocumentErrors.Empty}: refusing to store an empty claim piece.");

        return buffer.ToArray();
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
    /// to its own reference, so relocating or re-filing an object cannot make it decrypt elsewhere.
    /// </summary>
    private static byte[] AssociatedData(Guid tenantId, string storageRef)
        => Encoding.UTF8.GetBytes($"icd1|{tenantId:N}|{storageRef}");

    /// <summary>
    /// Parameters stripped and lower-cased: a browser sends <c>image/jpeg; charset=UTF-8</c> often
    /// enough that comparing the raw header against an allow-list refuses honest uploads.
    /// </summary>
    private static string NormalizeContentType(string? contentType)
    {
        if (string.IsNullOrWhiteSpace(contentType)) return string.Empty;

        var separator = contentType.IndexOf(';', StringComparison.Ordinal);

        return (separator < 0 ? contentType : contentType[..separator])
            .Trim()
            .ToLowerInvariant();
    }

    private DomainException IntegrityFailure(string storageRef, string reason, Exception? inner = null)
    {
        _logger.LogError(inner,
            "Claim object {StorageRef} failed integrity check: {Reason}", storageRef, reason);

        return new DomainException(
            $"{ClaimDocumentErrors.IntegrityFailure}: stored claim piece '{storageRef}' could not "
            + $"be authenticated ({reason}).");
    }

    private static byte[] ReadKey(string? configured)
    {
        const string setting = $"{ClaimStorageOptions.SectionName}:EncryptionKey";
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

/// <summary>Stable codes the store and the upload endpoint answer with.</summary>
internal static class ClaimDocumentErrors
{
    public const string Empty = "INSURANCE_CLAIM_DOCUMENT_EMPTY";

    public const string TooLarge = "INSURANCE_CLAIM_DOCUMENT_TOO_LARGE";

    public const string ContentTypeNotAllowed = "INSURANCE_CLAIM_DOCUMENT_CONTENT_TYPE_NOT_ALLOWED";

    public const string IntegrityFailure = "INSURANCE_CLAIM_DOCUMENT_INTEGRITY_FAILURE";

    /// <summary>
    /// The same file, by digest, is already on this dossier. A refusal and not a silent success:
    /// two rows for one piece would be two transmissions to the insurer.
    /// </summary>
    public const string Duplicate = "INSURANCE_CLAIM_DOCUMENT_DUPLICATE";

    public const string NotFound = "INSURANCE_CLAIM_DOCUMENT_NOT_FOUND";

    /// <summary>
    /// A download of a piece whose scan has not passed. Refused: handing an agent a file the
    /// antivirus has flagged — or not yet looked at — is the one thing the lock exists to prevent.
    /// </summary>
    public const string NotScanned = "INSURANCE_CLAIM_DOCUMENT_NOT_SCANNED";
}
