namespace Sankore.Modules.Integration.Infrastructure.RecordingStorage;

/// <summary>
/// Where a call recording's bytes live (TEL-B-16).
///
/// <para>
/// <b>Byte arrays and not streams, and the signature says so honestly.</b>
/// <see cref="System.Security.Cryptography.AesGcm"/> is single-shot in .NET and
/// <c>IObjectBackend</c> only knows <c>byte[]</c>, so the whole plaintext is materialised in the
/// process whatever this interface promised. Promising incrementality would be a lie a caller
/// would size its memory against — the same statement <c>ITelephonyPort.DownloadRecordingAsync</c>
/// makes. What bounds the exposure instead is a ceiling, in two places: the tenant's duration
/// policy and the host's byte ceiling.
/// </para>
/// </summary>
internal interface IRecordingStore
{
    /// <summary>
    /// Stores one recording and answers what the row must remember about it.
    /// </summary>
    Task<StoredRecording> StoreAsync(
        Guid tenantId, Guid callId, byte[] content, string contentType, CancellationToken ct);

    /// <summary>
    /// The decrypted bytes, or null when the object is absent or the reference is not this
    /// tenant's. <b>Throws</b> when the object exists and does not authenticate — see the store.
    /// </summary>
    Task<byte[]?> OpenAsync(Guid tenantId, string storageRef, CancellationToken ct);

    Task<bool> DeleteAsync(Guid tenantId, string storageRef, CancellationToken ct);
}

/// <param name="Sha256">
/// Digest of the PLAINTEXT, lower-case hex. On the row so a later read can prove the file is the
/// one that was stored — which for evidence in a dispute is the difference between a recording and
/// an audio file somebody has.
/// </param>
internal sealed record StoredRecording(
    string StorageRef, string ContentType, long SizeBytes, string Sha256);
