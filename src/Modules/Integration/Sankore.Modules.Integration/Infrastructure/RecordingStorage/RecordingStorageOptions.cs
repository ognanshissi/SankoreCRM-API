namespace Sankore.Modules.Integration.Infrastructure.RecordingStorage;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;

/// <summary>
/// Settings of the call-recording store, bound from <c>Telephony:Recordings</c> (TEL-B-16).
///
/// <para>
/// Host/deployment settings, NOT tenant parameters — the line <c>Kyc:Storage</c>,
/// <c>Integration:Batch:Storage</c> and <c>Integration:Insurance:ClaimStorage</c> all draw. A
/// tenant must not be able to lower the encryption bar or widen the size ceiling on a file that
/// holds a customer's voice. What a TENANT decides is in <c>tel_recording_policy</c>: whether to
/// keep anything at all, under what basis, for how long.
/// </para>
/// </summary>
internal sealed class RecordingStorageOptions
{
    public const string SectionName = "Telephony:Recordings";

    /// <summary>
    /// Root of the filesystem fallback. Leave empty in development; in any real environment point
    /// it at a mounted volume or configure a bucket — a recording written inside the container is
    /// gone on the next redeploy, and unlike a batch file it cannot be regenerated: the provider
    /// may well have deleted its own copy at our request.
    /// </summary>
    public string? BasePath { get; set; }

    /// <summary>
    /// Base64-encoded 32-byte AES-256-GCM key. The FIFTH key in this deployment, distinct from
    /// <c>Integration:FieldEncryptionKey</c> (command payload columns),
    /// <c>Integration:Batch:Storage:EncryptionKey</c> (outbound batch files),
    /// <c>Integration:Insurance:ClaimStorage:EncryptionKey</c> (<i>pièces de sinistre</i>) and
    /// <c>Kyc:Storage:EncryptionKey</c> (identity imagery). One compromised key must open one
    /// concern, and of the five this is the one whose contents a person's own voice is.
    /// Generate with <c>Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))</c>.
    /// </summary>
    public string? EncryptionKey { get; set; }

    /// <summary>
    /// Largest accepted recording, 50 MB by default — roughly an hour of the compressed mono audio
    /// a telephony provider produces.
    ///
    /// <para>
    /// A byte ceiling AND a duration ceiling exist, and they are not redundant: the duration is
    /// what a compliance officer reasons about and what the provider reports BEFORE a download
    /// starts, so it is what refuses a file cheaply (<c>TelRecordingPolicy.MaxDurationSeconds</c>);
    /// this one is the hard backstop for a provider that misreports, because
    /// <see cref="System.Security.Cryptography.AesGcm"/> is single-shot in .NET and the whole
    /// plaintext is in the process either way.
    /// </para>
    /// </summary>
    public long MaxBytes { get; set; } = 50L * 1024 * 1024;

    /// <summary>
    /// What a recording may be. An allow-list for the reason the claim store gives: the question
    /// is what a browser's audio player can open, and a type refused by name is a type renamed.
    /// <c>audio/mpeg</c> and <c>audio/wav</c> are what the providers in this family emit.
    /// </summary>
    public IList<string> AllowedContentTypes { get; set; } =
    [
        "audio/mpeg",
        "audio/mp3",
        "audio/wav",
        "audio/x-wav",
        "audio/ogg",
    ];
}

/// <summary>
/// Names this module's call-recording slot in the object store (TEL-B-16).
///
/// <para>
/// A FIFTH concern, separate from the batch files, the claim pieces and KYC imagery. A recording
/// has its own retention (frozen per file at retrieval, not per dossier), its own key and its own
/// audience — an agent reviewing a conversation or a compliance officer settling a dispute. One
/// bucket holding all of them would make a single leaked credential a leak of all of them.
/// </para>
/// </summary>
public static class RecordingStorage
{
    public const string Concern = "telephony-recordings";

    /// <summary>
    /// The filesystem root the host passes to <c>AddObjectBackend</c> as the fallback when no
    /// bucket is configured. Same shape as the batch and claim socles', which is where the
    /// bootstrapper enumerates every module's root.
    /// </summary>
    public static string ResolveBasePath(IConfiguration config, IHostEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(config);

        var configured = config[$"{RecordingStorageOptions.SectionName}:BasePath"];

        return string.IsNullOrWhiteSpace(configured)
            ? Path.Combine(AppContext.BaseDirectory, Concern)
            : Path.GetFullPath(configured);
    }
}
