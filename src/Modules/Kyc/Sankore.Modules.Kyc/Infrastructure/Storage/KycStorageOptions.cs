namespace Sankore.Modules.Kyc.Infrastructure.Storage;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

/// <summary>
/// Settings of the KYC evidence store, bound from the <c>Kyc:Storage</c> configuration section
/// (<see cref="SectionName"/>).
///
/// These are host/deployment settings, NOT tenant parameters: they belong in configuration and
/// user-secrets, never in <c>kyc_settings</c>. A tenant cannot be allowed to widen its own
/// accepted upload types or to lower the encryption bar.
/// </summary>
internal sealed class KycStorageOptions
{
    public const string SectionName = "Kyc:Storage";

    /// <summary>
    /// Root folder of the object store. Leave empty in development; in any real environment
    /// point it at a mounted volume, otherwise the evidence lives inside the container and a
    /// redeploy destroys documents a regulator may still ask for.
    /// Resolved by <see cref="ResolveBasePath(IConfiguration, IHostEnvironment)"/>.
    /// </summary>
    public string? BasePath { get; set; }

    /// <summary>
    /// Base64-encoded 32-byte AES-256-GCM key, separate from <c>Kyc:FieldEncryptionKey</c>:
    /// the column key and the object key protect data with different lifetimes and are rotated
    /// independently. Generate with
    /// <c>Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))</c>.
    /// </summary>
    public string? EncryptionKey { get; set; }

    /// <summary>
    /// Largest accepted payload, 10 MB by default — a phone photo of an ID card or a scanned
    /// PDF fits comfortably; anything larger is a mis-upload or an attempt to fill the volume.
    /// </summary>
    public long MaxBytes { get; set; } = 10L * 1024 * 1024;

    /// <summary>
    /// Accepted content types. Kept short on purpose: every extra type is one more parser the
    /// biometry provider and the back-office viewer must both handle. Overridable in
    /// configuration, because a country's ID authority eventually mails TIFFs.
    /// </summary>
    public IList<string> AllowedContentTypes { get; set; } =
        ["image/jpeg", "image/png", "application/pdf"];

    /// <summary>
    /// The object root for the host: <c>Kyc:Storage:BasePath</c> when configured (point it at a
    /// mounted volume), otherwise <c>kyc-documents/</c> under the content root. Mirrors
    /// <c>LocalSdkFileStore.ResolveBasePath</c>; the host calls it when binding these options, so
    /// the resolved path is visible in diagnostics instead of being recomputed inside a store.
    /// </summary>
    public static string ResolveBasePath(IConfiguration config, IHostEnvironment env)
    {
        var configured = config[$"{SectionName}:BasePath"];
        if (!string.IsNullOrWhiteSpace(configured)) return Path.GetFullPath(configured);

        return Path.Combine(env.ContentRootPath, DefaultFolderName);
    }

    /// <summary>
    /// The same root from the bound options alone, for a composition root that has no
    /// <see cref="IHostEnvironment"/> to consult.
    /// </summary>
    public static string ResolveBasePath(KycStorageOptions options, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (!string.IsNullOrWhiteSpace(options.BasePath)) return Path.GetFullPath(options.BasePath);

        // AppContext.BaseDirectory is the build output: it survives a restart but not a
        // redeploy. Acceptable for a dev box, never for an environment holding real evidence,
        // hence the warning — the host is expected to fill BasePath via the overload above.
        var fallback = Path.Combine(AppContext.BaseDirectory, DefaultFolderName);
        logger.LogWarning(
            "{Setting}:BasePath is not configured; KYC evidence will be written to {Fallback}, "
            + "which a redeploy destroys",
            SectionName, fallback);

        return fallback;
    }

    private const string DefaultFolderName = "kyc-documents";
}
