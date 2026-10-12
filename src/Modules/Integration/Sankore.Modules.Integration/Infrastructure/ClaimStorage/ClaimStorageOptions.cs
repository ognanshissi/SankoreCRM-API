namespace Sankore.Modules.Integration.Infrastructure.ClaimStorage;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

/// <summary>
/// Settings of the claim-document store, bound from <c>Integration:Insurance:ClaimStorage</c>.
///
/// <para>
/// Host/deployment settings, NOT tenant parameters — the line M02 draws for <c>Kyc:Storage</c> and
/// this module for its batch files. A tenant must not be able to lower the encryption bar, widen
/// the size ceiling or admit a content type on evidence a regulator may demand.
/// </para>
/// </summary>
internal sealed class ClaimStorageOptions
{
    public const string SectionName = "Integration:Insurance:ClaimStorage";

    /// <summary>
    /// Root of the filesystem fallback. Leave empty in development; in any real environment point
    /// it at a mounted volume or configure a bucket — a <i>pièce de sinistre</i> written inside
    /// the container is gone on the next redeploy, and it is the only copy of the police report
    /// the insurer is waiting for.
    /// </summary>
    public string? BasePath { get; set; }

    /// <summary>
    /// Base64-encoded 32-byte AES-256-GCM key. A FOURTH key in this deployment, distinct from
    /// <c>Integration:FieldEncryptionKey</c> (command payload columns),
    /// <c>Integration:Batch:Storage:EncryptionKey</c> (outbound batch files) and
    /// <c>Kyc:Storage:EncryptionKey</c> (identity imagery). One compromised key must open one
    /// concern, which is the whole reason they are separate settings and not one.
    /// Generate with <c>Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))</c>.
    /// </summary>
    public string? EncryptionKey { get; set; }

    /// <summary>
    /// Largest accepted piece, 20 MB by default — a photograph of a wrecked vehicle from a modern
    /// phone, or a scanned medical file of a few pages. Larger than the KYC ceiling (10 MB) for
    /// exactly that reason, and still a ceiling: <see cref="System.Security.Cryptography.AesGcm"/>
    /// is single-shot in .NET, so the whole plaintext is in the process either way and refusing is
    /// better than holding whatever somebody uploaded.
    /// </summary>
    public long MaxBytes { get; set; } = 20L * 1024 * 1024;

    /// <summary>
    /// What a <i>pièce de sinistre</i> may be. An allow-list and not a deny-list, because the
    /// question is what an insurer's back office can open — and because an executable refused by
    /// name is an executable renamed.
    ///
    /// <para>
    /// No <c>application/zip</c>, deliberately: an archive is a container whose contents the
    /// antivirus pass would have to walk and this platform does not, so admitting one would make
    /// the scan a formality. An agent sends the pages, not a folder.
    /// </para>
    /// </summary>
    public IList<string> AllowedContentTypes { get; set; } =
    [
        "image/jpeg",
        "image/png",
        "image/heic",
        "application/pdf",
    ];

    private const string DefaultFolderName = "insurance-claim-documents";

    /// <summary>
    /// The same root from the bound options alone, for a composition root with no
    /// <see cref="IHostEnvironment"/> to consult.
    /// </summary>
    public static string ResolveBasePath(ClaimStorageOptions options, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);

        if (!string.IsNullOrWhiteSpace(options.BasePath)) return Path.GetFullPath(options.BasePath);

        // AppContext.BaseDirectory is the build output: it survives a restart but not a redeploy.
        // Acceptable on a dev box, never for a deployment that actually receives claim pieces.
        var fallback = Path.Combine(AppContext.BaseDirectory, DefaultFolderName);

        logger.LogWarning(
            "{Setting}:BasePath is not configured; insurance claim documents will be written to "
            + "{Fallback}, which a redeploy destroys",
            SectionName, fallback);

        return fallback;
    }
}

/// <summary>
/// Names this module's claim-document slot in the object store.
///
/// <para>
/// A constant rather than a literal at each call site, for the reason
/// <c>IntegrationBatchStorage</c> and <c>KycObjectStorage</c> both state: it is the DI key of the
/// <see cref="Sankore.Shared.ObjectStorage.IObjectBackend"/> and, on a bucket deployment, the
/// bucket name — and "one bucket per concern" stops being true the moment two spellings of the
/// same word exist.
/// </para>
///
/// <para>
/// A THIRD concern in this deployment, separate from the batch files and from KYC imagery.
/// A claim piece has its own retention (ASS-12 counts from the dossier, not from an
/// acknowledgement a month later), its own key and its own audience — an insurer's claims unit
/// rather than a core banking system or a compliance officer. One bucket holding all three would
/// make a single leaked credential a leak of all three.
/// </para>
/// </summary>
public static class ClaimDocumentStorage
{
    public const string Concern = "insurance-claim-documents";

    /// <summary>
    /// The filesystem root the host passes to <c>AddObjectBackend</c> as the fallback when no
    /// bucket is configured. Same shape as the batch socle's, which is where the bootstrapper
    /// enumerates every module's root.
    /// </summary>
    public static string ResolveBasePath(IConfiguration config, IHostEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(config);

        var configured = config[$"{ClaimStorageOptions.SectionName}:BasePath"];

        return string.IsNullOrWhiteSpace(configured)
            ? Path.Combine(AppContext.BaseDirectory, Concern)
            : Path.GetFullPath(configured);
    }
}
