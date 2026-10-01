namespace Sankore.Modules.Kyc.Infrastructure.Biometry;

/// <summary>
/// Host configuration for the external biometric service, bound from the
/// <c>Kyc:Biometry</c> section. Everything here is deployment-wide: the per-tenant service token
/// lives in the vault instead (see <see cref="BiometrySecrets"/>).
/// </summary>
internal sealed class BiometryOptions
{
    public const string SectionName = "Kyc:Biometry";

    /// <summary>
    /// Hard ceiling applied to every timeout below. The HTTP client's resilience handler owns the
    /// retry budget, so a single attempt waiting minutes would only pile up queued verifications.
    /// </summary>
    public const int MaxTimeoutSeconds = 120;

    /// <summary>
    /// Root of the Flask service, e.g. <c>https://biometry.internal/</c>. Empty means not
    /// deployed: every call then answers <see cref="BiometryCodes.NotConfigured"/> as a technical
    /// result rather than throwing, so a tenant without biometry degrades instead of erroring.
    /// </summary>
    public string BaseUrl { get; set; } = string.Empty;

    /// <summary>
    /// Use <see cref="FakeBiometryClient"/> instead of the HTTP one. Read by the module's
    /// registration; it exists so a developer machine and the integration environment can run the
    /// whole KYC flow with no Flask deployment at all.
    /// </summary>
    public bool UseFake { get; set; }

    /// <summary>OCR is the slowest pass — it rasterises and runs a detector over a full page.</summary>
    public int OcrTimeoutSeconds { get; set; } = 30;

    public int FaceMatchTimeoutSeconds { get; set; } = 20;

    /// <summary>Scoring is arithmetic over readings we already hold; it should never be slow.</summary>
    public int ScoreTimeoutSeconds { get; set; } = 10;

    /// <summary>
    /// A biometric answer is a few kilobytes of JSON. The cap is what stops a misrouted URL
    /// (a login page, a proxy error, an image) from being buffered into the request's memory.
    /// </summary>
    public long MaxResponseBytes { get; set; } = 2L * 1024 * 1024;

    public TimeSpan OcrTimeout => Clamp(OcrTimeoutSeconds);

    public TimeSpan FaceMatchTimeout => Clamp(FaceMatchTimeoutSeconds);

    public TimeSpan ScoreTimeout => Clamp(ScoreTimeoutSeconds);

    /// <summary>
    /// Base address with a trailing slash. Without it <see cref="Uri"/> drops the last path
    /// segment of the configured URL, so a service mounted under <c>/biometry</c> would be called
    /// at the host root — a 404 that looks like a deployment problem.
    /// </summary>
    public Uri? ResolveBaseAddress()
    {
        if (string.IsNullOrWhiteSpace(BaseUrl))
            return null;

        var trimmed = BaseUrl.Trim();
        var withSlash = trimmed.EndsWith('/') ? trimmed : trimmed + "/";

        return Uri.TryCreate(withSlash, UriKind.Absolute, out var uri) ? uri : null;
    }

    private static TimeSpan Clamp(int seconds) =>
        TimeSpan.FromSeconds(Math.Clamp(seconds, 1, MaxTimeoutSeconds));
}
