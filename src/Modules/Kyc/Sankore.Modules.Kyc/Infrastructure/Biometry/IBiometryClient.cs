namespace Sankore.Modules.Kyc.Infrastructure.Biometry;

using Sankore.Modules.Kyc.Domain;

/// <summary>
/// The boundary in front of the external biometric service (OCR, face matching, scoring).
///
/// The service is a separate Flask deployment that owns the models; this module owns nothing of
/// its internals and may not assume it is reachable. Everything the orchestration needs in order
/// to decide what to do next is therefore expressed in <see cref="BiometryResult{T}"/>, never in
/// an exception type the caller would have to catch and classify.
///
/// <paramref name="correlationId"/> is the KYC verification's own id, echoed on every call so a
/// support request can be traced across the two deployments' logs.
/// </summary>
internal interface IBiometryClient
{
    /// <param name="declaredType">
    /// What the agent says they photographed. Required by the service — it picks the field
    /// template from it — and it is NOT derivable here: the document's type is something the OCR
    /// answer reports, so reading it first would need the very call it is an argument of.
    /// </param>
    Task<BiometryResult<OcrReading>> ReadDocumentAsync(
        Guid tenantId, BiometryImage image, KycDocumentType declaredType,
        string correlationId, CancellationToken ct);

    Task<BiometryResult<FaceMatch>> MatchFaceAsync(
        Guid tenantId, BiometryImage documentPortrait, BiometryImage selfie,
        string correlationId, CancellationToken ct);

    Task<BiometryResult<ConfidenceScore>> ScoreAsync(
        Guid tenantId, ScoreRequest request, string correlationId, CancellationToken ct);
}

/// <summary>
/// The three — and only three — things a call to the biometric service can mean.
///
/// They are kept apart because the KYC orchestration reacts to each of them differently, and
/// collapsing any two would silently break a file's lifecycle:
///
/// <list type="bullet">
/// <item><see cref="Success"/> — the service answered; the reading is usable evidence.</item>
/// <item><see cref="Rejected"/> — the service worked perfectly and says the <i>input</i> is not
/// usable (blurred photo, no face, unreadable document). This is a business fact: it is recorded
/// on the verification and the agent is asked for a better photo. Retrying the same bytes would
/// produce the same answer forever.</item>
/// <item><see cref="Unavailable"/> — the service could not answer at all (down, models still
/// loading, timeout, garbage response). Nothing is known about the input, so the file stays in
/// Verifying and a Hangfire replay tries again later. Recording this as a rejection would reject
/// an honest client because of our own outage.</item>
/// </list>
/// </summary>
internal enum BiometryOutcome
{
    Success = 0,
    Rejected = 1,
    Unavailable = 2,
}

/// <summary>
/// Outcome of one biometric call. Built only through <see cref="Success"/>,
/// <see cref="Rejected"/> and <see cref="Unavailable"/>, so a caller cannot construct a state
/// that is both — the distinction documented on <see cref="BiometryOutcome"/> is the whole point
/// of this type.
/// </summary>
internal sealed class BiometryResult<T>
    where T : class
{
    private readonly T? _payload;

    private BiometryResult(BiometryOutcome outcome, T? payload, string? code, string? detail)
    {
        Outcome = outcome;
        _payload = payload;
        Code = code;
        Detail = detail;
    }

    public BiometryOutcome Outcome { get; }

    /// <summary>Stable UPPER_SNAKE code, null only on success. See <see cref="BiometryCodes"/>.</summary>
    public string? Code { get; }

    /// <summary>Free-text context for logs and the audit trail. Never shown to a client.</summary>
    public string? Detail { get; }

    public bool IsSuccess => Outcome == BiometryOutcome.Success;

    /// <summary>The input is unusable — ask the agent for a better capture.</summary>
    public bool IsRejected => Outcome == BiometryOutcome.Rejected;

    /// <summary>We learned nothing — leave the file in Verifying and replay later.</summary>
    public bool IsUnavailable => Outcome == BiometryOutcome.Unavailable;

    public T Value => IsSuccess
        ? _payload!
        : throw new InvalidOperationException(
            $"Cannot read the payload of a {Outcome} biometry result. Code: {Code}");

    public static BiometryResult<T> Success(T payload) =>
        new(BiometryOutcome.Success, payload, null, null);

    public static BiometryResult<T> Rejected(string code, string? detail = null) =>
        new(BiometryOutcome.Rejected, null, code, detail);

    public static BiometryResult<T> Unavailable(string code, string? detail = null) =>
        new(BiometryOutcome.Unavailable, null, code, detail);
}

/// <summary>
/// Codes this module itself produces. The service may return others — they are passed through
/// verbatim, so this list is a vocabulary for the orchestration and the French labels, not a
/// closed set to validate against.
/// </summary>
internal static class BiometryCodes
{
    // ── Functional: the capture is at fault ──────────────────────────────────────────────
    public const string ImageQualityTooLow = "IMAGE_QUALITY_TOO_LOW";
    public const string NoFaceDetected = "NO_FACE_DETECTED";
    public const string DocumentUnreadable = "DOCUMENT_UNREADABLE";
    public const string MrzChecksumFailed = "MRZ_CHECKSUM_FAILED";

    // ── Technical: we are at fault, or the service is ────────────────────────────────────
    public const string ServiceUnavailable = "SERVICE_UNAVAILABLE";
    public const string ModelsNotReady = "MODELS_NOT_READY";
    public const string Timeout = "BIOMETRY_TIMEOUT";
    public const string ResponseTooLarge = "BIOMETRY_RESPONSE_TOO_LARGE";

    /// <summary>The service answered something we cannot read — a body, not a verdict.</summary>
    public const string UnexpectedResponse = "BIOMETRY_UNEXPECTED_RESPONSE";

    /// <summary>No base URL configured, or no service token in the vault for this tenant.</summary>
    public const string NotConfigured = "BIOMETRY_NOT_CONFIGURED";
}

/// <summary>
/// One image handed to the service. Bytes rather than a storage reference: the client must stay
/// ignorant of where KYC images live, and the caller already holds the decrypted bytes when it
/// decides to verify.
/// </summary>
internal sealed record BiometryImage(byte[] Content, string ContentType, string? FileName = null)
{
    /// <summary>Convenience for tests and for callers that already hold a base64 payload.</summary>
    public static BiometryImage FromBase64(string base64, string contentType = "image/jpeg") =>
        new(Convert.FromBase64String(base64), contentType);
}

/// <summary>What the OCR pass read off an identity document.</summary>
/// <param name="DocumentType">The service's own label ("CNI", "PASSPORT", "DRIVING_LICENCE"…).</param>
/// <param name="Fields">Extracted values, keyed by the service's field names, verbatim.</param>
/// <param name="FieldConfidences">0..1 per field of <paramref name="Fields"/>; a field may be absent here.</param>
/// <param name="Mrz">Machine-readable zone, null for a document that has none.</param>
/// <param name="ServiceVersion">
/// Stored with the verification: a decision must stay traceable to the model that produced it,
/// including after the Flask service is upgraded.
/// </param>
/// <param name="Raw">
/// The service's own answer, kept verbatim so <see cref="IBiometryClient.ScoreAsync"/> can hand it
/// straight back. The scorer is stateless and the contract is explicit about it — "le module .NET
/// renvoie tels quels les résultats de /v1/ocr et /v1/face-match" — and the projection above is
/// LOSSY: it drops the per-field source, the anomalies, the image quality and the MRZ checks, all
/// of which the score is computed from. Rebuilding a request out of this record would therefore
/// have the scorer grade a different document than the one that was read.
/// <para>Null only for <see cref="FakeBiometryClient"/>, which never reaches a real scorer.</para>
/// </param>
internal sealed record OcrReading(
    string DocumentType,
    IReadOnlyDictionary<string, string> Fields,
    IReadOnlyDictionary<string, double> FieldConfidences,
    MrzReading? Mrz,
    string ServiceVersion,
    Generated.OcrResponse? Raw = null);

/// <summary>
/// Machine-readable zone. <paramref name="ChecksumValid"/> false is reported as a reading, not as
/// a rejection: the service did its job, and whether a failed checksum blocks the file is M02's
/// decision, taken on the whole verification rather than on this one call.
/// </summary>
internal sealed record MrzReading(
    string? Raw,
    bool ChecksumValid,
    IReadOnlyDictionary<string, string> Fields);

/// <summary>Result of comparing the document portrait with the selfie.</summary>
/// <param name="Similarity">0..1 as the model reports it.</param>
/// <param name="IsMatch">The service's own verdict against its threshold — we do not re-derive it.</param>
/// <param name="Threshold">
/// The threshold <paramref name="IsMatch"/> was decided against, as the service reports it. Shown
/// to a reviewer asking why 0.71 was a match and 0.69 was not — never used to re-derive the
/// verdict, which stays the model's.
/// </param>
/// <param name="Raw">The service's own answer, for the scorer. Same reason as on <see cref="OcrReading"/>.</param>
internal sealed record FaceMatch(
    double Similarity,
    bool IsMatch,
    double PortraitQuality,
    double SelfieQuality,
    string ModelVersion,
    string ServiceVersion,
    double Threshold = 0,
    Generated.FaceMatchResponse? Raw = null);

/// <summary>Everything the stateless scorer needs; it keeps no session between our calls.</summary>
/// <param name="DeclaredFields">
/// What the agent typed, so the service can cross-check it against the OCR reading. Keys are the
/// same field names the OCR uses.
/// </param>
/// <param name="Channel">
/// How the customer was enrolled. Required by the service and used in its weighting — a remote
/// capture is not graded like one taken at a counter — so it is the KYC file's own channel,
/// mapped, rather than a constant.
/// </param>
/// <param name="RiskLevel">
/// The file's vigilance level. Required for the same reason: the scorer is stricter on a
/// high-risk file.
/// </param>
/// <param name="CorrectedFieldNames">
/// Names of the fields the agent corrected after the reading, which the scorer penalises. Names
/// only — the service asks for no values, and this record reaches neither the audit trail nor a
/// log, but a date of birth has no business travelling where it is not needed.
/// </param>
internal sealed record ScoreRequest(
    OcrReading? Ocr,
    FaceMatch? FaceMatch,
    KycChannel Channel,
    KycVigilanceLevel RiskLevel,
    IReadOnlyList<string>? CorrectedFieldNames = null);

/// <summary>The global verdict.</summary>
/// <param name="Score">0..100.</param>
/// <param name="Breakdown">Per-criterion contribution, keyed by the service's criterion names.</param>
/// <param name="Flags">Service-raised warnings ("EXPIRED_DOCUMENT", "NAME_MISMATCH"…).</param>
internal sealed record ConfidenceScore(
    int Score,
    BiometryConfidenceLevel Level,
    IReadOnlyDictionary<string, int> Breakdown,
    IReadOnlyList<string> Flags,
    string ServiceVersion);

internal enum BiometryConfidenceLevel
{
    Low = 0,
    Medium = 1,
    High = 2,
}

/// <summary>
/// The single definition of score → level, shared by the HTTP client's fallback and by
/// <see cref="FakeBiometryClient"/>. Two copies would let a test pass against a level the real
/// client would never produce for the same score.
/// </summary>
internal static class BiometryConfidenceLevels
{
    public static BiometryConfidenceLevel FromScore(int score) => score switch
    {
        < 50 => BiometryConfidenceLevel.Low,
        < 80 => BiometryConfidenceLevel.Medium,
        _ => BiometryConfidenceLevel.High,
    };
}
