namespace Sankore.Modules.Integration.Infrastructure.ClaimStorage;

/// <summary>
/// Store for <i>pièces de sinistre</i> (ASS-09, criterion 1; ASS-12 on their protection).
///
/// <para>
/// Write once, read, delete — the same narrow contract <c>IKycDocumentStore</c> has and for the
/// same reason: a claim piece is never modified in place, it is superseded by another upload, so
/// an <c>UpdateAsync</c> would only invite overwriting the object a validator's decision or an
/// insurer's transmission was taken on.
/// </para>
/// </summary>
internal interface IClaimDocumentStore
{
    /// <summary>
    /// Encrypts <paramref name="content"/> and stores it, returning what the caller persists on
    /// the <c>ins_claim_document</c> row. Throws <see cref="Sankore.Shared.Kernel.DomainException"/>
    /// when the content type is not an accepted piece type, the payload is empty or it is too
    /// large — upload-time abuse cases, not business outcomes of a handler.
    /// </summary>
    Task<StoredClaimDocument> StoreAsync(
        Guid tenantId, Guid claimId, Stream content, string contentType, CancellationToken ct);

    /// <summary>
    /// The plaintext bytes, or <c>null</c> when the reference is unknown, malformed, or was not
    /// issued for <paramref name="tenantId"/>. Null rather than an exception: a reference travels
    /// through URLs, so "not yours" and "does not exist" must be indistinguishable — the same
    /// rule M01's out-of-perimeter read follows.
    ///
    /// <para>
    /// Bytes and not a <c>Stream</c>, unlike the KYC store: both readers of this need the whole
    /// buffer anyway — the antivirus pass feeds it to a scanner in one go, and AES-GCM is
    /// single-shot in .NET so the plaintext is materialised regardless. Handing back a stream
    /// would suggest an incrementality that does not exist.
    /// </para>
    /// </summary>
    Task<byte[]?> OpenAsync(Guid tenantId, string storageRef, CancellationToken ct);

    /// <summary>
    /// Removes the object. <c>false</c> when there was nothing to remove, so a retention sweep and
    /// an upload's own cleanup can both run idempotently.
    /// </summary>
    Task<bool> DeleteAsync(Guid tenantId, string storageRef, CancellationToken ct);
}

/// <param name="Sha256">
/// Digest of the PLAINTEXT, lower-case hex. It is in <c>ux_ins_claim_document_digest</c>, so it is
/// what makes the same file uploaded twice for one dossier a refusal rather than a second
/// transmission to the insurer — and what lets a later check prove the piece behind a decision was
/// never swapped.
/// </param>
internal sealed record StoredClaimDocument(
    string StorageRef, string ContentType, long SizeBytes, string Sha256);
