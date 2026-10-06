namespace Sankore.Modules.Kyc.Infrastructure.Biometry;

using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Sankore.Modules.Kyc.Infrastructure.Biometry.Generated;
using Sankore.Modules.Kyc.Infrastructure.Crypto;
using Sankore.Shared.Infrastructure.Crypto;

/// <summary>
/// Keeps the biometric service's own answers for a later <c>/v1/score</c> call — encrypted, and
/// without the generated types leaking into the feature slices.
///
/// <para>
/// <c>/v1/score</c> is stateless and takes the answers to <c>/v1/ocr</c> and
/// <c>/v1/face-match</c> back verbatim. The module's evidence tables keep only projections of
/// them, so re-scoring after a field correction had nothing faithful to send: the OCR payload it
/// could rebuild dropped the per-field source, the anomalies, the image quality and the MRZ
/// checks, which is most of what the score is computed from.
/// </para>
///
/// <para>
/// Encrypted with the module's own key (<see cref="KycFieldProtection"/>), because the OCR payload
/// repeats the document number in <c>fields</c> and again in <c>mrz</c> — the one value the
/// evidence row exists to protect, which is why the plain jsonb column beside it has the number
/// stripped out.
/// </para>
/// </summary>
internal sealed class BiometryPayloadProtector(
    [FromKeyedServices(KycFieldProtection.Key)] IFieldEncryptor encryptor,
    ILogger<BiometryPayloadProtector> logger)
{
    /// <summary>
    /// Compact and culture-free. The payload is round-tripped through the generated DTOs, whose
    /// own attributes carry the wire names, so no naming policy is set here.
    /// </summary>
    private static readonly JsonSerializerOptions JsonOpts = new();

    public string? Protect(OcrResponse? payload) => ProtectCore(payload);

    public string? Protect(FaceMatchResponse? payload) => ProtectCore(payload);

    public OcrResponse? UnprotectOcr(string? ciphertext) => UnprotectCore<OcrResponse>(ciphertext);

    public FaceMatchResponse? UnprotectFace(string? ciphertext) =>
        UnprotectCore<FaceMatchResponse>(ciphertext);

    /// <summary>
    /// Puts the agent's correction into the payload the scorer is about to re-read.
    ///
    /// <para>
    /// Without this the scorer would re-grade the value the machine MISREAD, and the correction
    /// would change the stored fields, the audit trail and nothing else — the score would come back
    /// identical and the agent would reasonably conclude the feature is broken.
    /// </para>
    ///
    /// <para>
    /// The confidence is set to 1: an agent read the value off the document with their own eyes,
    /// which is better evidence than the model's guess. The source is marked <c>AGENT</c> so the
    /// service can tell a corrected field from one it read itself — it penalises corrections, and
    /// that penalty is the point.
    /// </para>
    /// </summary>
    public static void ApplyCorrection(OcrResponse payload, string fieldName, string newValue)
    {
        payload.Fields ??= new Dictionary<string, FieldValue>(StringComparer.Ordinal);

        if (payload.Fields.TryGetValue(fieldName, out var existing) && existing is not null)
        {
            existing.Value = newValue;
            existing.Confidence = 1;
            existing.Low_confidence = false;
            existing.Source = AgentSource;
            return;
        }

        payload.Fields[fieldName] = new FieldValue
        {
            Value = newValue,
            Confidence = 1,
            Low_confidence = false,
            Source = AgentSource,
        };
    }

    /// <summary>Not one of the service's own values ("VISUAL", "MRZ") — it is ours, by design.</summary>
    private const string AgentSource = "AGENT";

    private string? ProtectCore<T>(T? payload)
        where T : class
        => payload is null ? null : encryptor.Encrypt(JsonSerializer.Serialize(payload, JsonOpts));

    /// <summary>
    /// Null for anything unusable — no row, a payload written before this column existed, a key
    /// that no longer decrypts it, a shape the generated DTO has since outgrown. The caller then
    /// behaves exactly as it does for a missing payload: it refuses to score rather than scoring
    /// something it had to invent.
    /// </summary>
    private T? UnprotectCore<T>(string? ciphertext)
        where T : class
    {
        if (string.IsNullOrWhiteSpace(ciphertext))
            return null;

        try
        {
            var json = encryptor.Decrypt(ciphertext);

            return string.IsNullOrWhiteSpace(json)
                ? null
                : JsonSerializer.Deserialize<T>(json, JsonOpts);
        }
        // InvalidOperationException is what AesGcmFieldEncryptor raises for a malformed payload or
        // a failed authentication tag; the other two are a bad base64 and a shape the DTO outgrew.
        catch (Exception ex) when (ex is JsonException
                                      or FormatException
                                      or InvalidOperationException
                                      or System.Security.Cryptography.CryptographicException)
        {
            logger.LogWarning(
                ex, "A stored biometry payload could not be read back; the file will not be re-scored");

            return null;
        }
    }
}
