namespace Sankore.Shared.ObjectStorage;

/// <summary>
/// Puts and gets opaque bytes under an opaque key. Nothing more.
///
/// <para>
/// It is deliberately this small. The KYC document store encrypts with AES-GCM before a byte ever
/// reaches a backend, validates the shape of its references, enforces content types and sizes, and
/// hashes the plaintext — none of which is the storage medium's business. Keeping those above this
/// line is what lets the medium change without the guarantees changing with it: moving KYC images
/// to Cloudflare R2 must not quietly downgrade "encrypted by us, before it leaves the process" to
/// "encrypted at rest by the provider", which protects against a stolen disk and not against a
/// leaked API key.
/// </para>
///
/// <para>
/// The key is built by the caller and is the only namespace there is: a bucket has no notion of a
/// tenant, so tenant isolation is a key prefix the caller owns and the backend never interprets.
/// Keys are <c>/</c>-separated and must satisfy <see cref="ObjectKey.Validate"/> — see there for
/// why a backend validates rather than sanitises.
/// </para>
/// </summary>
public interface IObjectBackend
{
    /// <summary>
    /// Writes the object.
    ///
    /// <para>
    /// Callers generate unique keys, so a collision is a bug rather than a case to handle; a
    /// backend that can detect one for free refuses it instead of overwriting (the filesystem
    /// can, an S3 PUT cannot). Nothing may depend on either behaviour.
    /// </para>
    /// </summary>
    Task PutAsync(string objectKey, byte[] content, CancellationToken ct = default);

    /// <summary>
    /// The bytes, or <c>null</c> when the key holds nothing — absence is never an exception, as a
    /// key travels through URLs and "not yours" must be indistinguishable from "not there".
    ///
    /// <para>
    /// <paramref name="maxBytes"/> is a refusal, not a truncation: an object bigger than that
    /// cannot have been written by the caller that is asking for it, so the backend logs it and
    /// answers <c>null</c> rather than allocating whatever the medium happens to hold. It is a
    /// parameter rather than a check on the returned array because the whole point is to decide
    /// BEFORE the bytes are in memory — the filesystem reads the length, S3 reads
    /// <c>Content-Length</c>, and neither costs an extra round trip.
    /// </para>
    /// </summary>
    Task<byte[]?> GetAsync(string objectKey, long maxBytes, CancellationToken ct = default);

    /// <summary><c>true</c> when something was removed, <c>false</c> when the key held nothing.</summary>
    Task<bool> DeleteAsync(string objectKey, CancellationToken ct = default);

    /// <summary>
    /// Keys under a prefix, in no guaranteed order. Needed to migrate objects between backends,
    /// and nothing else — no request path should ever enumerate a bucket.
    /// </summary>
    IAsyncEnumerable<string> ListAsync(string keyPrefix, CancellationToken ct = default);
}
