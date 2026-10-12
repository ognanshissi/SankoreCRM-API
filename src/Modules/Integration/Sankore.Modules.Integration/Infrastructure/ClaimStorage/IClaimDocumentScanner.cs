namespace Sankore.Modules.Integration.Infrastructure.ClaimStorage;

using Sankore.Modules.Integration.Domain;

/// <summary>
/// The antivirus pass a <i>pièce de sinistre</i> must survive before it may leave the platform
/// (ASS-09, criterion 1).
///
/// <para>
/// A port and not a concrete client, because the scanner is a deployment's choice — a clamd on the
/// same host, a sidecar, a scanning appliance — and because the one thing that must never depend
/// on that choice is the GATE: <c>ClaimDocument.IsTransmittable</c> is true only for
/// <see cref="DocumentScanStatus.Clean"/>, so a deployment with no scanner transmits nothing
/// rather than transmitting everything.
/// </para>
/// </summary>
internal interface IClaimDocumentScanner
{
    /// <summary>
    /// Verdict on one piece's PLAINTEXT bytes.
    ///
    /// <para>
    /// It answers a verdict and never throws for an outage: the difference between
    /// <see cref="DocumentScanStatus.Infected"/> and
    /// <see cref="DocumentScanStatus.ScanFailed"/> is the difference between the customer's
    /// file being dangerous and our own service being down, and recording the second as the first
    /// would tell an agent their client sent malware because a daemon was restarting. The same
    /// separation M02 makes between a biometry <c>Rejected</c> and a biometry <c>Unavailable</c>.
    /// </para>
    /// </summary>
    Task<ClaimScanVerdict> ScanAsync(byte[] plaintext, CancellationToken ct);
}

/// <param name="Detail">
/// What the scanner reported — a signature name, a connection error. NEVER the file's content, and
/// it lands in <c>ins_claim_document.scan_detail</c> which is capped at 300 characters and shown
/// to an operator.
/// </param>
internal sealed record ClaimScanVerdict(DocumentScanStatus Status, string? Detail)
{
    internal static ClaimScanVerdict Clean() => new(DocumentScanStatus.Clean, null);

    internal static ClaimScanVerdict Infected(string signature)
        => new(DocumentScanStatus.Infected, signature);

    internal static ClaimScanVerdict Failed(string reason)
        => new(DocumentScanStatus.ScanFailed, reason);
}

/// <summary>
/// The default when no scanner is configured: every piece comes back
/// <see cref="DocumentScanStatus.ScanFailed"/>.
///
/// <para>
/// <b>It fails CLOSED, and that is the whole reason it exists rather than the registration being
/// left absent.</b> With no registration at all the scan job would not resolve and the pieces
/// would sit at <c>Pending</c> for ever with nothing saying why; with a double that answered
/// <c>Clean</c> the antivirus pass would be a decoration on a deployment that has none. Answering
/// "the scanner told us nothing" is the truth, it keeps <c>IsTransmittable</c> false, and it puts
/// the reason in <c>scan_detail</c> where an operator reads it.
/// </para>
///
/// <para>
/// It logs once per piece at WARNING, deliberately noisy: a deployment that distributes insurance
/// and has configured no scanner is a deployment whose dossiers will never reach their insurer,
/// and that should be loud long before somebody asks why.
/// </para>
/// </summary>
internal sealed class UnconfiguredClaimDocumentScanner(
    Microsoft.Extensions.Logging.ILogger<UnconfiguredClaimDocumentScanner> logger)
    : IClaimDocumentScanner
{
    internal const string Reason = "No antivirus scanner is configured for claim pieces.";

    public Task<ClaimScanVerdict> ScanAsync(byte[] plaintext, CancellationToken ct)
    {
        Microsoft.Extensions.Logging.LoggerExtensions.LogWarning(
            logger,
            "A claim piece of {SizeBytes} bytes could not be scanned: {Reason} Configure "
            + "{Setting}:Address to let dossiers reach their insurer",
            plaintext?.Length ?? 0, Reason, ClamAvOptions.SectionName);

        return Task.FromResult(ClaimScanVerdict.Failed(Reason));
    }
}
