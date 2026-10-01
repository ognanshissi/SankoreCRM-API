namespace Sankore.Modules.Kyc.Infrastructure.Storage;

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
    /// Resolved by <see cref="LocalKycDocumentStore.ResolveBasePath"/>.
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
}
