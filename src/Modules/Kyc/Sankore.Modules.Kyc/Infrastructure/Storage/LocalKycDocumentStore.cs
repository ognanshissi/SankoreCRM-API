namespace Sankore.Modules.Kyc.Infrastructure.Storage;

using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Sankore.Shared.Kernel;

/// <summary>
/// Filesystem implementation of <see cref="IKycDocumentStore"/>, encrypting every object with
/// AES-256-GCM before it touches the disk. The sibling of
/// <c>Leads.Features.LeadSources.Sdk.LocalSdkFileStore</c>: same "local first, behind an
/// interface, configurable root" shape, so swapping in S3/MinIO later touches one class.
///
/// On-disk layout — <c>{basePath}/{tenantToken}/{ab}/{objectId}.kycobj</c>, file content
/// <c>"KYC1" | nonce(12) | tag(16) | ciphertext</c>:
/// <list type="bullet">
/// <item>the tenant folder makes a mis-scoped read a missing file rather than a leak, and lets
/// ops delete one tenant's evidence with a single <c>rm -rf</c> on offboarding;</item>
/// <item>the two-character shard keeps directories listable when a branch uploads tens of
/// thousands of documents;</item>
/// <item>the tenant id and the ref are fed to GCM as associated data, so moving an object file
/// into another tenant's folder does not make it readable there — the tag check fails.</item>
/// </list>
/// </summary>
internal sealed class LocalKycDocumentStore : IKycDocumentStore
{
    private const int NonceSize = 12;
    private const int TagSize = 16;
    private const int KeySize = 32;
    private const string ObjectExtension = ".kycobj";
    private const string DefaultFolderName = "kyc-documents";

    /// <summary>Format marker, so a future layout change is detectable instead of silently garbage.</summary>
    private static readonly byte[] Magic = "KYC1"u8.ToArray();

    private static readonly int HeaderSize = Magic.Length + NonceSize + TagSize;

    /// <summary>
    /// The exact shape <see cref="StoreAsync"/> issues: <c>k1.{16 hex}.{32 hex}</c>. Anything
    /// else is rejected before a path is built, which is how path traversal is handled here —
    /// not by sanitising a caller-supplied name, but by never accepting one.
    /// <c>\z</c> rather than <c>$</c> deliberately: <c>$</c> also matches before a trailing
    /// newline, and <c>"k1.….\n/../etc/passwd"</c> must not slip through on the next change.
    /// </summary>
    private static readonly Regex StorageRefShape = new(
        @"^k1\.[0-9a-f]{16}\.[0-9a-f]{32}\z",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private readonly KycStorageOptions _options;
    private readonly ILogger<LocalKycDocumentStore> _logger;
    private readonly byte[] _key;
    private readonly string _basePath;

    public LocalKycDocumentStore(
        IOptions<KycStorageOptions> options,
        ILogger<LocalKycDocumentStore> logger)
    {
        _options = options.Value;
        _logger = logger;

        // Validated eagerly, unlike AesGcmFieldEncryptor which reads its key lazily: a missing
        // key there has already bitten us as "ArgumentNullException (Parameter 's')" thrown by
        // Convert.FromBase64String in the middle of a request, with nothing in the message
        // naming the setting. Failing at construction turns that into a startup error that
        // says which configuration key to set.
        _key = ReadKey(_options.EncryptionKey);
        _basePath = ResolveBasePath(_options, _logger);
    }

    /// <summary>
    /// Resolves the object-store root for the host: <c>Kyc:Storage:BasePath</c> when configured
    /// (point it at a mounted volume), otherwise <c>kyc-documents/</c> under the content root.
    /// Mirrors <c>LocalSdkFileStore.ResolveBasePath</c>; the host calls it when binding
    /// <see cref="KycStorageOptions"/> so the resolved path is visible in diagnostics.
    /// </summary>
    public static string ResolveBasePath(IConfiguration config, IHostEnvironment env)
    {
        var configured = config[$"{KycStorageOptions.SectionName}:BasePath"];
        if (!string.IsNullOrWhiteSpace(configured)) return Path.GetFullPath(configured);

        return Path.Combine(env.ContentRootPath, DefaultFolderName);
    }

    public async Task<KycStoredDocument> StoreAsync(
        Guid tenantId, Guid kycFileId, KycDocumentKind kind,
        Stream content, string contentType, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(content);

        var normalizedContentType = NormalizeContentType(contentType);
        if (!_options.AllowedContentTypes.Contains(normalizedContentType, StringComparer.OrdinalIgnoreCase))
            throw new DomainException(
                $"KYC_DOCUMENT_CONTENT_TYPE_NOT_ALLOWED: '{contentType}' is not an accepted KYC "
                + $"evidence type ({string.Join(", ", _options.AllowedContentTypes)}).");

        var plaintext = await ReadBoundedAsync(content, ct);

        var nonce = RandomNumberGenerator.GetBytes(NonceSize);
        var ciphertext = new byte[plaintext.Length];
        var tag = new byte[TagSize];

        // objectId is random, never derived from kycFileId: the ref ends up in URLs and in the
        // browser, and two refs must not reveal that they belong to the same KYC file. kycFileId
        // and kind therefore only reach the logs — they stay in the signature because the layout
        // may later shard per file, and callers should not have to change then.
        var storageRef = $"k1.{TenantToken(tenantId)}.{Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant()}";

        using (var aes = new AesGcm(_key, TagSize))
        {
            aes.Encrypt(nonce, plaintext, ciphertext, tag, AssociatedData(tenantId, storageRef));
        }

        var path = BuildPath(TenantToken(tenantId), storageRef);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        // Write aside then move: a crash mid-write would otherwise leave a truncated object that
        // only surfaces much later, as a GCM tag mismatch indistinguishable from tampering.
        var temporaryPath = path + ".tmp";
        await using (var file = File.Create(temporaryPath))
        {
            await file.WriteAsync(Magic, ct);
            await file.WriteAsync(nonce, ct);
            await file.WriteAsync(tag, ct);
            await file.WriteAsync(ciphertext, ct);
        }
        File.Move(temporaryPath, path, overwrite: false);

        var sha256 = Convert.ToHexString(SHA256.HashData(plaintext)).ToLowerInvariant();

        _logger.LogInformation(
            "Stored KYC document {Kind} for file {KycFileId} (tenant {TenantId}): {SizeBytes} bytes, {ContentType}",
            kind, kycFileId, tenantId, plaintext.Length, normalizedContentType);

        return new KycStoredDocument(storageRef, normalizedContentType, plaintext.Length, sha256);
    }

    public async Task<Stream?> OpenAsync(Guid tenantId, string storageRef, CancellationToken ct)
    {
        if (!TryResolvePath(tenantId, storageRef, out var path))
        {
            // Debug, not Warning: a stale ref after a tenant switch is normal traffic. A genuine
            // probing attempt shows up as a burst of 404s at the endpoint layer.
            _logger.LogDebug("Rejected KYC storage ref for tenant {TenantId}", tenantId);
            return null;
        }

        var info = new FileInfo(path);
        if (!info.Exists) return null;

        // An object larger than the write-side limit cannot have been produced by this store;
        // refuse it rather than allocating whatever is on disk.
        if (info.Length < HeaderSize || info.Length > _options.MaxBytes + HeaderSize)
        {
            _logger.LogWarning(
                "KYC object {StorageRef} has an impossible length of {Length} bytes, ignoring",
                storageRef, info.Length);
            return null;
        }

        var raw = await File.ReadAllBytesAsync(path, ct);
        var plaintext = Decrypt(raw, tenantId, storageRef);

        return new MemoryStream(plaintext, 0, plaintext.Length, writable: false, publiclyVisible: false);
    }

    /// <summary>
    /// Kept out of the async method on purpose: the spans below must not be hoisted into a state
    /// machine, and the decrypt is pure CPU work on a buffer that is already in memory.
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
            // Never degrade this to "not found": the object exists and does not authenticate,
            // which means a swapped image, a bit-rotted volume or the wrong key — all three are
            // incidents compliance has to hear about, not empty results.
            throw IntegrityFailure(storageRef, "authentication tag mismatch", ex);
        }

        return plaintext;
    }

    public Task<bool> DeleteAsync(Guid tenantId, string storageRef, CancellationToken ct)
    {
        if (!TryResolvePath(tenantId, storageRef, out var path) || !File.Exists(path))
            return Task.FromResult(false);

        File.Delete(path);
        _logger.LogInformation("Deleted KYC object {StorageRef} for tenant {TenantId}", storageRef, tenantId);
        return Task.FromResult(true);
    }

    /// <summary>
    /// Reads at most <see cref="KycStorageOptions.MaxBytes"/> and gives up the moment the stream
    /// goes beyond it, so an endless upload never becomes an endless allocation. A seekable
    /// stream is measured first and refused without a single byte read. The whole plaintext does
    /// end up in memory — <see cref="AesGcm"/> is single-shot in .NET, there is no streaming
    /// GCM — but bounded by that same limit, which is why the limit is checked here and not
    /// after the copy.
    /// </summary>
    private async Task<byte[]> ReadBoundedAsync(Stream content, CancellationToken ct)
    {
        var max = _options.MaxBytes;

        if (content.CanSeek && content.Length - content.Position > max)
            throw TooLarge(content.Length - content.Position, max);

        using var buffer = new MemoryStream(capacity: 64 * 1024);
        var chunk = new byte[64 * 1024];
        long total = 0;

        int read;
        while ((read = await content.ReadAsync(chunk, ct)) > 0)
        {
            total += read;
            if (total > max) throw TooLarge(total, max);
            buffer.Write(chunk, 0, read);
        }

        if (total == 0)
            throw new DomainException("KYC_DOCUMENT_EMPTY: the uploaded KYC document is empty.");

        return buffer.ToArray();
    }

    private bool TryResolvePath(Guid tenantId, string storageRef, out string path)
    {
        path = string.Empty;
        if (string.IsNullOrEmpty(storageRef) || !StorageRefShape.IsMatch(storageRef)) return false;

        var token = TenantToken(tenantId);

        // The tenant segment is derived from the tenant id, so a ref issued for another tenant
        // simply does not match — no lookup table, nothing to forget to filter on.
        if (!string.Equals(storageRef.Split('.')[1], token, StringComparison.Ordinal)) return false;

        var candidate = BuildPath(token, storageRef);

        // Belt and braces behind the regex: if a future edit loosens the shape, the escape still
        // has to get past this. Keep both.
        if (!candidate.StartsWith(_basePath + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            return false;

        path = candidate;
        return true;
    }

    private string BuildPath(string tenantToken, string storageRef)
    {
        var objectId = storageRef.Split('.')[2];
        return Path.Combine(_basePath, tenantToken, objectId[..2], objectId + ObjectExtension);
    }

    /// <summary>
    /// Stable per-tenant folder name. A hash rather than the raw GUID so the ref — which the
    /// caller hands to a browser — carries no tenant identifier to correlate on.
    /// </summary>
    private static string TenantToken(Guid tenantId)
        => Convert.ToHexString(SHA256.HashData(tenantId.ToByteArray()))[..16].ToLowerInvariant();

    /// <summary>
    /// GCM associated data: authenticated, not encrypted. Binds the ciphertext to the tenant and
    /// to its own ref, so relocating or renaming an object file cannot make it decrypt elsewhere.
    /// </summary>
    private static byte[] AssociatedData(Guid tenantId, string storageRef)
        => Encoding.UTF8.GetBytes($"kyc1|{tenantId:N}|{storageRef}");

    private static string NormalizeContentType(string? contentType)
    {
        if (string.IsNullOrWhiteSpace(contentType)) return string.Empty;

        // Browsers send "image/jpeg; charset=…" on some multipart uploads; the parameters are
        // not part of the allow-list decision.
        var semicolon = contentType.IndexOf(';', StringComparison.Ordinal);
        var media = semicolon >= 0 ? contentType[..semicolon] : contentType;
        return media.Trim().ToLowerInvariant();
    }

    private static DomainException TooLarge(long size, long max)
        => new($"KYC_DOCUMENT_TOO_LARGE: the document is {size} bytes, the limit is {max} bytes.");

    private DomainException IntegrityFailure(string storageRef, string reason, Exception? inner = null)
    {
        _logger.LogError(inner, "KYC object {StorageRef} failed integrity check: {Reason}", storageRef, reason);
        return new DomainException(
            $"KYC_DOCUMENT_INTEGRITY_FAILURE: stored KYC document '{storageRef}' could not be "
            + $"authenticated ({reason}).");
    }

    private static string ResolveBasePath(KycStorageOptions options, ILogger logger)
    {
        if (!string.IsNullOrWhiteSpace(options.BasePath)) return Path.GetFullPath(options.BasePath);

        // AppContext.BaseDirectory is the build output: it survives a restart but not a
        // redeploy. Acceptable for a dev box, never for an environment holding real evidence,
        // hence the warning — the host is expected to fill BasePath via ResolveBasePath.
        var fallback = Path.Combine(AppContext.BaseDirectory, DefaultFolderName);
        logger.LogWarning(
            "{Setting}:BasePath is not configured; KYC evidence will be written to {Fallback}, "
            + "which a redeploy destroys",
            KycStorageOptions.SectionName, fallback);

        return fallback;
    }

    private static byte[] ReadKey(string? configured)
    {
        const string setting = $"{KycStorageOptions.SectionName}:EncryptionKey";
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
                $"{setting} must be exactly {KeySize} bytes once Base64-decoded (AES-256), "
                + $"but {key.Length} were provided. {guidance}");

        return key;
    }
}
