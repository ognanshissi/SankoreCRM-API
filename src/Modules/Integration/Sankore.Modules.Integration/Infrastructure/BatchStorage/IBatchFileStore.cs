namespace Sankore.Modules.Integration.Infrastructure.BatchStorage;

/// <summary>
/// The platform's own copy of a batch file, encrypted at rest (INT-24, criterion 5).
///
/// <para>
/// An interface and not a direct <see cref="Sankore.Shared.ObjectStorage.IObjectBackend"/> call
/// for the reason that interface's own documentation gives: the encryption, the shape of a
/// reference and the size ceiling live ABOVE the medium, so moving from a mounted volume to a
/// bucket changes which backend is injected and nothing else. In particular it does not trade
/// "encrypted by us, before the bytes leave the process" for "encrypted at rest by the provider",
/// which is a different promise to a regulator and a much weaker one against a leaked API key.
/// </para>
///
/// <para>
/// What a batch file holds is the whole reason that matters: a day of one tenant's outbound
/// customer writes — names, identity-document numbers, addresses, declared income — in a single
/// flat file the CBS can read. The copy we keep for evidence must not be readable by anything
/// that can list the volume.
/// </para>
/// </summary>
internal interface IBatchFileStore
{
    /// <summary>
    /// Encrypts and stores <paramref name="plaintext"/>, answering the opaque reference that goes
    /// into <c>integration_batch_file.storage_ref</c>.
    /// </summary>
    Task<string> StoreAsync(Guid tenantId, byte[] plaintext, CancellationToken ct);

    /// <summary>
    /// The plaintext back, or <c>null</c> when the reference holds nothing — a file already
    /// purged, or a reference that was never this tenant's.
    /// </summary>
    Task<byte[]?> OpenAsync(Guid tenantId, string storageRef, CancellationToken ct);

    /// <summary>
    /// Deletes the content. <c>true</c> when something was removed. The ROW is kept by the
    /// caller: the checksum and the record count are what let an inspection a year later confirm
    /// what was sent without keeping the personal data in it.
    /// </summary>
    Task<bool> DeleteAsync(Guid tenantId, string storageRef, CancellationToken ct);
}
