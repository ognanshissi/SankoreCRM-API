namespace Sankore.Modules.Kyc.Features.PublicRemote;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Sankore.Modules.Kyc.Domain;
using Sankore.Modules.Kyc.Infrastructure;
using Sankore.Modules.Kyc.Infrastructure.Biometry;
using Sankore.Modules.Kyc.Infrastructure.Settings;
using Sankore.Modules.Kyc.Infrastructure.Storage;

/// <summary>
/// Tells the customer immediately whether the photograph they just took is usable (KRM-B-06).
///
/// <para>
/// <b>Why this exists at all.</b> Without it a customer photographs four documents, sends them, and
/// learns two days later from an agent that the first one was blurred. The whole value of a remote
/// journey is that the person holding the card is still holding it when they find out.
/// </para>
///
/// <para>
/// <b>It is a GATE, not the evidence.</b> The authoritative OCR runs at submission, through
/// <c>RunKycVerificationCommand</c>, which is the only thing in this module that writes
/// <c>KycIdentityDocument</c>. KRM-B-06 asks for the upload-time reading to be kept and reused;
/// it is deliberately not, because two ways of producing the same evidence is one too many — the
/// second one would bypass the field corrections, the duplicate detection and the scoring that the
/// command performs around the reading. The cost is one extra OCR call per document; the benefit is
/// that there is exactly one path from a photograph to a verified identity.
/// </para>
///
/// <para>
/// <b>An outage accepts the photograph.</b> A customer must never be stopped by our own
/// unavailability — they would retake the same usable picture until their budget ran out. The check
/// is skipped, the upload stands, and the verification at submission is where the service's absence
/// is actually handled (the file waits in <c>Verifying</c> and Hangfire replays it).
/// </para>
/// </summary>
internal sealed class KycRemoteQualityGate(
    KycDbContext db,
    IBiometryClient biometry,
    IKycDocumentStore store,
    IKycSettings settings,
    ILogger<KycRemoteQualityGate> logger)
{
    /// <param name="Refused">
    /// True when the customer should retake it. False both for a usable photograph and for one the
    /// service could not judge.
    /// </param>
    /// <param name="Causes">
    /// What to fix, as the service names it. Empty when there is nothing to say.
    /// </param>
    internal sealed record Verdict(bool Refused, string? Code, IReadOnlyList<string> Causes)
    {
        internal static readonly Verdict Accepted = new(false, null, []);
    }

    /// <summary>
    /// Inspects one freshly captured image. Never throws: every failure is an acceptance.
    /// </summary>
    internal async Task<Verdict> InspectAsync(
        KycRemoteInvitation invitation,
        KycDocumentKind kind,
        byte[] bytes,
        string contentType,
        CancellationToken ct)
    {
        // Past the budget the gate stops refusing. The retake count stays on the invitation and
        // reaches the validator, so a document that took nine attempts is visible as such — which
        // is a fact about the evidence rather than a reason to strand the customer.
        var maxRetries = await settings.GetIntAsync(
            invitation.TenantId, KycSettingKeys.RemoteMaxQualityRetries, ct);

        if (invitation.QualityRetries().GetValueOrDefault(kind.ToString()) >= maxRetries)
        {
            logger.LogInformation(
                "Quality gate skipped for {Kind} on KYC file {KycFileId}: retake budget exhausted",
                kind, invitation.KycFileId);
            return Verdict.Accepted;
        }

        var correlationId = Guid.NewGuid().ToString();

        try
        {
            return kind switch
            {
                KycDocumentKind.IdentityDocumentFront or KycDocumentKind.IdentityDocumentBack
                    => await InspectDocumentAsync(invitation, bytes, contentType, correlationId, ct),

                KycDocumentKind.Selfie
                    => await InspectSelfieAsync(invitation, bytes, contentType, correlationId, ct),

                // A proof of address is a utility bill: no MRZ, no face, no number to extract, and
                // it never enters the OCR or face-match chain. There is nothing to check here, and
                // inventing a check would refuse a perfectly good quittance.
                _ => Verdict.Accepted,
            };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The client is built to return outcomes rather than throw, so reaching this is a bug
            // in it — and a bug in our quality gate must not become a customer who cannot enrol.
            logger.LogError(ex,
                "Quality gate threw for {Kind} on KYC file {KycFileId}; accepting the upload",
                kind, invitation.KycFileId);
            return Verdict.Accepted;
        }
    }

    private async Task<Verdict> InspectDocumentAsync(
        KycRemoteInvitation invitation,
        byte[] bytes,
        string contentType,
        string correlationId,
        CancellationToken ct)
    {
        // The declared type matters for the real reading, not for "can this be read at all" — and
        // at this point the customer may not have a stored front to infer it from. CNI is the
        // service's own default and the overwhelming majority of documents in this market; the
        // authoritative call at submission uses what the customer actually declared.
        var reading = await biometry.ReadDocumentAsync(
            invitation.TenantId,
            new BiometryImage(bytes, contentType),
            KycDocumentType.Cni,
            correlationId, ct);

        return reading.Outcome switch
        {
            // Nothing from the reading is returned — not a field, not a confidence, not the
            // document type the service believes it saw. The customer can read their own card; what
            // they need from us is "retake it" or "that one is fine".
            BiometryOutcome.Success => Verdict.Accepted,

            BiometryOutcome.Rejected => Refusal(reading.Code, reading.Detail),

            // Unavailable: see the class remarks.
            _ => Unavailable(reading.Code),
        };
    }

    private async Task<Verdict> InspectSelfieAsync(
        KycRemoteInvitation invitation,
        byte[] bytes,
        string contentType,
        string correlationId,
        CancellationToken ct)
    {
        // The only way to ask "is there exactly one face here" is the face-match call, which needs
        // something to compare against — hence the endpoint's rule that the identity document comes
        // first. The MATCH VERDICT IS DISCARDED: whether this person is the person on the card is
        // the institution's judgement, taken on the whole file by a validator or by the scoring
        // policy, and telling the customer would both pre-empt that and hand an impostor a
        // feedback loop.
        var portrait = await LoadCurrentFrontAsync(invitation, ct);

        if (portrait is null)
        {
            // The endpoint checks this first, so reaching it means the stored object has gone. The
            // selfie is accepted rather than refused: the customer did nothing wrong.
            logger.LogWarning(
                "No stored identity document to check the selfie of KYC file {KycFileId} against",
                invitation.KycFileId);
            return Verdict.Accepted;
        }

        var match = await biometry.MatchFaceAsync(
            invitation.TenantId,
            portrait,
            new BiometryImage(bytes, contentType),
            correlationId, ct);

        return match.Outcome switch
        {
            BiometryOutcome.Success => Verdict.Accepted,

            // Only the two codes about the CAPTURE reach the customer. Any other rejection — a
            // portrait the service could not use, for instance — is about the document rather than
            // the selfie, and refusing the selfie for it would send the customer retaking the wrong
            // photograph.
            BiometryOutcome.Rejected when IsSelfieCaptureCode(match.Code)
                => Refusal(match.Code, match.Detail),

            BiometryOutcome.Rejected => Verdict.Accepted,

            _ => Unavailable(match.Code),
        };
    }

    /// <summary>
    /// The current front of the identity document, decrypted for the comparison.
    /// </summary>
    private async Task<BiometryImage?> LoadCurrentFrontAsync(
        KycRemoteInvitation invitation, CancellationToken ct)
    {
        var current = await db.KycDocuments
            .IgnoreQueryFilters()
            .Where(d => d.TenantId == invitation.TenantId
                     && d.KycFileId == invitation.KycFileId
                     && d.Kind == KycDocumentKind.IdentityDocumentFront)
            .OrderByDescending(d => d.UploadedAt)
            .Select(d => new { d.StorageRef, d.ContentType })
            .FirstOrDefaultAsync(ct);

        if (current is null) return null;

        await using var stream = await store.OpenAsync(invitation.TenantId, current.StorageRef, ct);
        if (stream is null) return null;

        using var buffer = new MemoryStream();
        await stream.CopyToAsync(buffer, ct);

        return new BiometryImage(buffer.ToArray(), current.ContentType);
    }

    /// <summary>
    /// The two codes that mean "retake the selfie". Everything else the face-match call can refuse
    /// is about the document.
    /// </summary>
    private static bool IsSelfieCaptureCode(string? code)
        => code is BiometryCodes.NoFaceDetected or MultipleFacesDetected;

    /// <summary>
    /// Not in <c>BiometryCodes</c>: that class documents itself as the vocabulary this MODULE
    /// produces, and the service may return others verbatim. This one is the service's, and it is
    /// named here because the selfie gate has to match on it.
    /// </summary>
    private const string MultipleFacesDetected = "MULTIPLE_FACES_DETECTED";

    private Verdict Refusal(string? code, string? detail)
        => new(Refused: true, Code: code, Causes: SplitCauses(detail));

    private Verdict Unavailable(string? code)
    {
        logger.LogInformation(
            "Quality gate could not reach the biometric service ({Code}); accepting the upload",
            code);
        return Verdict.Accepted;
    }

    /// <summary>
    /// The service puts its reasons in a free-text detail. Split into a list so the front can turn
    /// each into one instruction instead of showing a sentence it did not write.
    /// </summary>
    private static IReadOnlyList<string> SplitCauses(string? detail)
        => string.IsNullOrWhiteSpace(detail)
            ? []
            : [.. detail.Split([',', ';'], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)];
}
