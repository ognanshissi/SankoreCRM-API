namespace Sankore.Modules.Kyc.Infrastructure.Storage;

/// <summary>
/// Store for KYC evidence (identity-document scans, selfies). KYC-owned on purpose: the
/// shared <see cref="Sankore.Shared.Kernel.IFileStore"/> is a plain folder for transient
/// import files — no encryption at rest, no content type, no tenant partitioning — which is
/// exactly what a regulator will ask about for a copy of someone's ID card.
///
/// The contract is deliberately narrow (write once, read, delete): KYC evidence is never
/// modified in place, it is replaced by a new upload, so an <c>UpdateAsync</c> would only
/// invite overwriting the object a previous verification decision was taken on.
/// </summary>
internal interface IKycDocumentStore
{
    /// <summary>
    /// Encrypts <paramref name="content"/> and stores it, returning the opaque reference the
    /// caller persists on the KYC file. Throws <see cref="Sankore.Shared.Kernel.DomainException"/>
    /// when the content type is not an accepted evidence type or the payload is too large —
    /// those are upload-time programming/abuse cases, not business outcomes of a handler.
    /// </summary>
    Task<KycStoredDocument> StoreAsync(
        Guid tenantId, Guid kycFileId, KycDocumentKind kind,
        Stream content, string contentType, CancellationToken ct);

    /// <summary>
    /// Opens the decrypted content, or <c>null</c> when <paramref name="storageRef"/> is
    /// unknown, malformed, or was not issued for <paramref name="tenantId"/>. Null rather
    /// than an exception on purpose: a ref travels through URLs and payloads, so "not yours"
    /// and "does not exist" must be indistinguishable to the caller — the same reason a read
    /// outside the agency perimeter returns CLIENT_NOT_FOUND in M01.
    /// </summary>
    Task<Stream?> OpenAsync(Guid tenantId, string storageRef, CancellationToken ct);

    /// <summary>
    /// Removes the object. <c>false</c> when there was nothing to remove (unknown ref, or a
    /// ref belonging to another tenant) so a retention sweep can run idempotently.
    /// </summary>
    Task<bool> DeleteAsync(Guid tenantId, string storageRef, CancellationToken ct);
}

/// <summary>
/// Public like the module's other domain vocabulary (M01 does the same with
/// <c>ClientStatus</c>): the store itself stays internal, only the words it is addressed with
/// are public — which also lets xUnit, whose test methods must be public, use them as
/// <c>[Theory]</c> parameters.
/// </summary>
public enum KycDocumentKind
{
    IdentityDocumentFront,
    IdentityDocumentBack,
    Selfie
}

/// <summary>
/// What the caller persists about a stored object. <paramref name="Sha256"/> is the digest of
/// the <em>plaintext</em>: a later verification can re-read the object and prove the image
/// behind a validated KYC decision was never swapped.
/// </summary>
public sealed record KycStoredDocument(
    string StorageRef, string ContentType, long SizeBytes, string Sha256);
