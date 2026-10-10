namespace Sankore.Modules.Integration.Infrastructure.BatchStorage;

using Microsoft.Extensions.Logging;

/// <summary>
/// Settings of the batch-file store, bound from <c>Integration:Batch:Storage</c>.
///
/// <para>
/// Host/deployment settings, NOT tenant parameters — the same line M02 draws for
/// <c>Kyc:Storage</c>. A tenant must not be able to lower the encryption bar or widen the size
/// ceiling of a file that carries its own customers' identity data.
/// </para>
/// </summary>
internal sealed class IntegrationBatchStorageOptions
{
    public const string SectionName = "Integration:Batch:Storage";

    /// <summary>
    /// Root folder of the filesystem fallback. Leave empty in development; in any real
    /// environment point it at a mounted volume or configure a bucket instead — a batch file
    /// written inside the container is gone on the next redeploy, and with it the only copy of
    /// what was deposited at the IMF.
    /// </summary>
    public string? BasePath { get; set; }

    /// <summary>
    /// Base64-encoded 32-byte AES-256-GCM key. A THIRD key, distinct from
    /// <c>Integration:FieldEncryptionKey</c> (which protects command payload columns) and from
    /// <c>Kyc:Storage:EncryptionKey</c>: a batch file aggregates many customers' data in one
    /// object, so one compromised key must not open both the columns and the files.
    /// Generate with <c>Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))</c>.
    /// </summary>
    public string? EncryptionKey { get; set; }

    /// <summary>
    /// Largest accepted file, 32 MB by default. A day of one tenant's CBS writes is kilobytes;
    /// 32 MB is the ceiling at which something has gone wrong — a runaway loop, a payload that is
    /// not a payload — and refusing is better than holding it in memory, since
    /// <see cref="System.Security.Cryptography.AesGcm"/> is single-shot in .NET and the whole
    /// plaintext is in the process either way.
    /// </summary>
    public long MaxBytes { get; set; } = 32L * 1024 * 1024;

    private const string DefaultFolderName = "integration-batch-files";

    /// <summary>
    /// The same root from the bound options alone, for a composition root with no
    /// <see cref="IHostEnvironment"/> to consult.
    /// </summary>
    public static string ResolveBasePath(IntegrationBatchStorageOptions options, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);

        if (!string.IsNullOrWhiteSpace(options.BasePath)) return Path.GetFullPath(options.BasePath);

        // AppContext.BaseDirectory is the build output: it survives a restart but not a redeploy.
        // Acceptable on a dev box, never for a deployment that actually deposits files.
        var fallback = Path.Combine(AppContext.BaseDirectory, DefaultFolderName);
        logger.LogWarning(
            "{Setting}:BasePath is not configured; integration batch files will be written to "
            + "{Fallback}, which a redeploy destroys",
            SectionName, fallback);

        return fallback;
    }
}
