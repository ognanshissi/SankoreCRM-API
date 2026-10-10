namespace Sankore.Modules.Integration.Infrastructure.Transport;

using Sankore.Modules.Integration.Domain;
using Sankore.Modules.Integration.PublicApi;

/// <summary>
/// Moving a batch file to and from an external system (INT-24, INT-25).
///
/// <para>
/// An interface and not a concrete SFTP client because the specification gives the deposit two
/// routes: "le dépôt se fait par SFTP, <b>directement ou via l'agent relais</b>". Those are the
/// same operation over two different carriers — one opens an outbound connection from this
/// process, the other hands the bytes to an agent that already holds the only connection into the
/// IMF's network. The caller must not branch on which; the connection's
/// <see cref="IntegrationMode"/> decides, and the registration resolves it.
/// </para>
///
/// <para>
/// Every method returns an <see cref="IntegrationResult"/>: a host that is down, a directory that
/// does not exist, a key the server refuses — all are outcomes a batch job records and retries,
/// never exceptions that fail a Hangfire job with a stack trace nobody maps back to a tenant.
/// </para>
/// </summary>
internal interface IIntegrationFileTransport
{
    /// <summary>
    /// Writes one file into the connection's outbound directory.
    ///
    /// <para>
    /// The content is the PLAINTEXT of the file. Encryption at rest protects our own copy in the
    /// object store; what the external system receives has to be what it can read.
    /// </para>
    /// </summary>
    Task<IntegrationResult> PutAsync(
        IntegrationConnection connection, string fileName, byte[] content, CancellationToken ct);

    /// <summary>Names present in the inbound directory, oldest first where the server says so.</summary>
    Task<IntegrationResult<IReadOnlyList<string>>> ListInboundAsync(
        IntegrationConnection connection, CancellationToken ct);

    /// <summary>
    /// Reads one inbound file. <paramref name="maxBytes"/> is a REFUSAL and not a truncation: a
    /// half-read acknowledgement file would close the wrong commands.
    /// </summary>
    Task<IntegrationResult<byte[]>> GetInboundAsync(
        IntegrationConnection connection, string fileName, long maxBytes, CancellationToken ct);

    /// <summary>
    /// Moves a processed inbound file out of the polling directory.
    ///
    /// <para>
    /// Archiving rather than deleting, because a file we misread is the only evidence of what the
    /// external system actually sent. The sequence check already stops a re-processed file from
    /// being applied twice, so this is about leaving an audit trail, not about correctness.
    /// </para>
    /// </summary>
    Task<IntegrationResult> ArchiveInboundAsync(
        IntegrationConnection connection, string fileName, CancellationToken ct);
}
