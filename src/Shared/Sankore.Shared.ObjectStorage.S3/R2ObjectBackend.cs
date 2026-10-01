namespace Sankore.Shared.ObjectStorage;

using System.Net;
using System.Runtime.CompilerServices;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using Microsoft.Extensions.Logging;

/// <summary>
/// Cloudflare R2 backend: one bucket per concern, the key being the object key verbatim.
///
/// <para>
/// R2 speaks the S3 API, so this is the AWS S3 client pointed at an R2 endpoint — and the keys it
/// holds are the same <c>/</c>-separated keys <see cref="LocalObjectBackend"/> writes as paths.
/// That is what makes the migration a copy: a key listed from a volume is found in a bucket, and a
/// half-finished move still serves every document from whichever side holds it.
/// </para>
///
/// <para>
/// <b>It encrypts nothing.</b> KYC evidence reaches <see cref="PutAsync"/> already sealed with
/// AES-256-GCM by <c>KycDocumentStore</c>, and that must not be traded for the provider's
/// encryption at rest: server-side encryption protects a stolen disk, our own encryption also
/// protects a leaked API token — which is the credential this class holds. Enabling
/// <c>ServerSideEncryptionMethod</c> here would be a reasonable <em>addition</em> one day; it is
/// never a replacement, and the bytes must keep arriving encrypted either way.
/// </para>
/// </summary>
public sealed class R2ObjectBackend : IObjectBackend, IDisposable
{
    private readonly IAmazonS3 _s3;
    private readonly bool _ownsClient;
    private readonly string _bucketName;
    private readonly ILogger<R2ObjectBackend> _logger;

    /// <summary>
    /// Builds the S3 client from <paramref name="options"/>. One instance per bucket: a bucket is
    /// a concern (<c>kyc-documents</c>, <c>imports</c>), and a backend that could be re-pointed at
    /// another one would make the DI key a suggestion rather than a boundary.
    /// </summary>
    public R2ObjectBackend(
        ObjectStorageOptions options, string bucketName, ILogger<R2ObjectBackend> logger)
        : this(CreateClient(options), bucketName, logger, ownsClient: true)
    {
    }

    /// <summary>
    /// Takes an already-built client — for a test double, and for a host that wants to share one
    /// <see cref="IAmazonS3"/> across concerns. The caller keeps ownership of it.
    /// </summary>
    public R2ObjectBackend(IAmazonS3 s3, string bucketName, ILogger<R2ObjectBackend> logger)
        : this(s3, bucketName, logger, ownsClient: false)
    {
    }

    private R2ObjectBackend(
        IAmazonS3 s3, string bucketName, ILogger<R2ObjectBackend> logger, bool ownsClient)
    {
        ArgumentNullException.ThrowIfNull(s3);
        ArgumentException.ThrowIfNullOrWhiteSpace(bucketName);
        ArgumentNullException.ThrowIfNull(logger);

        _s3 = s3;
        _bucketName = bucketName;
        _logger = logger;
        _ownsClient = ownsClient;
    }

    /// <summary>Exposed for diagnostics and for the migration, which reports where it wrote to.</summary>
    public string BucketName => _bucketName;

    /// <summary>
    /// The S3 client configured for R2.
    ///
    /// <para>
    /// <c>ForcePathStyle</c> because R2 addresses a bucket as <c>&lt;endpoint&gt;/&lt;bucket&gt;</c>
    /// and has no virtual-host form for the per-account endpoint; <c>AuthenticationRegion =
    /// "auto"</c> because R2 has one region and SigV4 still needs a region in the credential scope.
    /// </para>
    /// </summary>
    private static AmazonS3Client CreateClient(ObjectStorageOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();

        var config = new AmazonS3Config
        {
            ServiceURL = options.ResolveServiceUrl(),
            ForcePathStyle = true,
            AuthenticationRegion = "auto",

            // KNOWN R2 INTEROP ISSUE, not a preference.
            //
            // AWS SDK v4 defaults both of these to WHEN_SUPPORTED, which puts an
            // `x-amz-checksum-crc32` header on every PutObject and expects one back. R2 has not
            // consistently accepted or returned those trailers, and the failure is a signature or
            // checksum mismatch on upload rather than anything naming a checksum. WHEN_REQUIRED
            // restores the v3 behaviour: send a checksum only where the API mandates one.
            //
            // This costs nothing in integrity terms here — every object this backend stores is
            // AES-GCM sealed by its caller, and the GCM tag detects corruption that a CRC32 would
            // merely flag.
            RequestChecksumCalculation = RequestChecksumCalculation.WHEN_REQUIRED,
            ResponseChecksumValidation = ResponseChecksumValidation.WHEN_REQUIRED,
        };

        return new AmazonS3Client(
            new BasicAWSCredentials(options.AccessKeyId, options.SecretAccessKey), config);
    }

    /// <summary>
    /// Writes the object, refusing a key that already holds one.
    ///
    /// <para>
    /// The refusal is <c>If-None-Match: *</c> — a conditional write R2 honours and the AWS SDK
    /// surfaces as <see cref="PutObjectRequest.IfNoneMatch"/>. It is the only form that is atomic:
    /// a HEAD-then-PUT would leave a window in which two writers both see nothing and the second
    /// silently replaces the first, and replacing KYC evidence a decision was taken on is the one
    /// outcome this method exists to prevent.
    /// </para>
    ///
    /// <para>
    /// No <c>CannedACL</c> and no <c>StorageClass</c>: R2 rejects ACLs outright, and its single
    /// storage class makes the header meaningless. Setting either turns every write into a 400.
    /// </para>
    /// </summary>
    public async Task PutAsync(string objectKey, byte[] content, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(content);
        ObjectKey.Validate(objectKey);

        // Seekable, so the SDK can sign the payload and set Content-Length without buffering again.
        using var body = new MemoryStream(content, writable: false);

        var request = new PutObjectRequest
        {
            BucketName = _bucketName,
            Key = objectKey,
            InputStream = body,
            ContentType = "application/octet-stream",
            IfNoneMatch = "*",

            // The SDK closes InputStream when it is done; this one is ours and `using` owns it.
            AutoCloseStream = false,
        };

        try
        {
            await _s3.PutObjectAsync(request, ct);
        }
        catch (AmazonS3Exception ex) when (IsPreconditionFailure(ex))
        {
            // Matches InMemoryObjectBackend and the IOException the filesystem raises on a
            // non-overwriting move: the contract is "throws", the exception type is not part of it.
            throw new InvalidOperationException(
                $"Object key '{objectKey}' is already in use in bucket '{_bucketName}'.", ex);
        }
    }

    /// <summary>
    /// <inheritdoc cref="IObjectBackend.GetAsync"/>
    ///
    /// <para>
    /// The ceiling is decided from the response's <c>Content-Length</c> header, before the body is
    /// touched: disposing the response aborts the transfer, so an oversized object costs headers
    /// and nothing more. Fetching then measuring would make <paramref name="maxBytes"/> a lie —
    /// the allocation it exists to prevent would already have happened.
    /// </para>
    /// </summary>
    public async Task<byte[]?> GetAsync(string objectKey, long maxBytes, CancellationToken ct = default)
    {
        // A key arrives from a URL; a malformed one must read as "nothing here", never as an
        // exception that tells a prober their guess was at least well formed.
        if (!ObjectKey.IsValid(objectKey)) return null;

        GetObjectResponse response;
        try
        {
            response = await _s3.GetObjectAsync(
                new GetObjectRequest { BucketName = _bucketName, Key = objectKey }, ct);
        }
        catch (AmazonS3Exception ex) when (IsMissingKey(ex))
        {
            return null;
        }

        using (response)
        {
            var length = response.ContentLength;

            if (length > maxBytes)
            {
                _logger.LogWarning(
                    "Object {ObjectKey} in bucket {Bucket} is {Length} bytes, beyond the {MaxBytes} the caller accepts; refusing to read it",
                    objectKey, _bucketName, length, maxBytes);
                return null;
            }

            if (length >= 0)
            {
                var buffer = new byte[length];
                await response.ResponseStream.ReadExactlyAsync(buffer, ct);
                return buffer;
            }

            // No Content-Length: S3 and R2 always send one for a whole-object GET, so this is a
            // proxy rewriting the response rather than the store. Read anyway, but stop one byte
            // past the ceiling rather than trusting a header that is not there.
            return await ReadBoundedAsync(response.ResponseStream, objectKey, maxBytes, ct);
        }
    }

    /// <summary>
    /// <inheritdoc cref="IObjectBackend.DeleteAsync"/>
    ///
    /// <para>
    /// The HEAD is not redundant. <c>DeleteObject</c> answers 204 whether or not the key existed —
    /// S3 deletes are idempotent by design — so the only way to answer the contract's question
    /// ("was something removed?") is to ask first. It costs a round trip on a path that runs once
    /// per document, and the alternative is a backend that reports <c>true</c> for a key it never
    /// held, which would make a migration's "deleted from source" count meaningless.
    /// </para>
    /// </summary>
    public async Task<bool> DeleteAsync(string objectKey, CancellationToken ct = default)
    {
        if (!ObjectKey.IsValid(objectKey)) return false;

        try
        {
            await _s3.GetObjectMetadataAsync(
                new GetObjectMetadataRequest { BucketName = _bucketName, Key = objectKey }, ct);
        }
        catch (AmazonS3Exception ex) when (IsMissingKey(ex))
        {
            return false;
        }

        await _s3.DeleteObjectAsync(
            new DeleteObjectRequest { BucketName = _bucketName, Key = objectKey }, ct);

        return true;
    }

    /// <summary>
    /// <inheritdoc cref="IObjectBackend.ListAsync"/>
    ///
    /// <para>
    /// The prefix is matched server-side and is deliberately NOT run through
    /// <see cref="ObjectKey"/>: a prefix is not a key. <c>"t1/"</c> ends in a slash and
    /// <c>""</c> is empty — both are refused as keys and both are legitimate prefixes, and the
    /// filesystem backend treats them the same way.
    /// </para>
    /// </summary>
    public async IAsyncEnumerable<string> ListAsync(
        string keyPrefix, [EnumeratorCancellation] CancellationToken ct = default)
    {
        keyPrefix ??= string.Empty;

        string? continuationToken = null;

        do
        {
            ct.ThrowIfCancellationRequested();

            var page = await _s3.ListObjectsV2Async(
                new ListObjectsV2Request
                {
                    BucketName = _bucketName,
                    Prefix = keyPrefix,
                    ContinuationToken = continuationToken,
                },
                ct);

            foreach (var entry in page.S3Objects ?? [])
            {
                ct.ThrowIfCancellationRequested();

                // A "directory marker" — a zero-byte object whose key ends in '/', which some
                // tools create. It is not an object any Get can resolve, and handing one to the
                // migration would look like a document that vanished.
                if (string.IsNullOrEmpty(entry.Key) || entry.Key[^1] == '/') continue;

                yield return entry.Key;
            }

            // IsTruncated is bool? in SDK v4; a missing value means "this was the last page".
            continuationToken = page.IsTruncated == true ? page.NextContinuationToken : null;
        }
        while (!string.IsNullOrEmpty(continuationToken));
    }

    public void Dispose()
    {
        if (_ownsClient) _s3.Dispose();
    }

    private async Task<byte[]?> ReadBoundedAsync(
        Stream stream, string objectKey, long maxBytes, CancellationToken ct)
    {
        if (maxBytes < 0) return null;

        using var buffer = new MemoryStream();
        var chunk = new byte[81920];

        while (true)
        {
            var read = await stream.ReadAsync(chunk, ct);
            if (read == 0) break;

            if (buffer.Length + read > maxBytes)
            {
                _logger.LogWarning(
                    "Object {ObjectKey} in bucket {Bucket} exceeds the {MaxBytes} the caller accepts (no Content-Length was returned); refusing to read it",
                    objectKey, _bucketName, maxBytes);
                return null;
            }

            buffer.Write(chunk, 0, read);
        }

        return buffer.ToArray();
    }

    /// <summary>
    /// A key that is not there.
    ///
    /// <para>
    /// <c>NoSuchBucket</c> is excluded on purpose, even though it also arrives as a 404: a
    /// mistyped bucket name would otherwise read as "every document is absent" and a migration
    /// would report an empty source as success. A missing bucket is a configuration fault and
    /// must surface as one.
    /// </para>
    /// </summary>
    private static bool IsMissingKey(AmazonS3Exception ex) =>
        ex.StatusCode == HttpStatusCode.NotFound
        && !string.Equals(ex.ErrorCode, "NoSuchBucket", StringComparison.Ordinal);

    /// <summary>
    /// The conditional write lost. R2 and S3 answer 412 <c>PreconditionFailed</c>; some
    /// S3-compatible endpoints answer 409 <c>ConditionalRequestConflict</c> for a concurrent
    /// conditional PUT. Both mean the same thing to the caller: the key was taken.
    /// </summary>
    private static bool IsPreconditionFailure(AmazonS3Exception ex) =>
        ex.StatusCode == HttpStatusCode.PreconditionFailed
        || ex.StatusCode == HttpStatusCode.Conflict;
}
