namespace Sankore.Modules.Kyc.Infrastructure.Biometry;

/// <summary>
/// In-process stand-in for the external biometric service.
///
/// It exists for two audiences and must stay pleasant for both: the rest of M02's tests, which
/// want one line to force a given outcome, and a developer or integration environment running the
/// whole KYC flow with no Flask deployment (<see cref="BiometryOptions.UseFake"/>).
///
/// Everything it answers is a pure function of its own properties — no clock, no randomness, no
/// hashing of the image bytes. A test that asserts on a field must keep passing on the next run
/// and on someone else's machine; "plausible but varying" data is how a suite starts flaking.
/// </summary>
internal sealed class FakeBiometryClient : IBiometryClient
{
    public const string ServiceVersion = "fake-biometry-1.0.0";
    public const string FaceModelVersion = "fake-arcface-1.0.0";

    /// <summary>Similarity above which the default answer calls it a match, absent an override.</summary>
    public const double DefaultMatchThreshold = 0.80;

    /// <summary>Forces every endpoint to reject functionally — "the capture is unusable".</summary>
    public static FakeBiometryClient Rejecting(string code) => new()
    {
        ForcedDocumentReading = BiometryResult<OcrReading>.Rejected(code),
        ForcedFaceMatch = BiometryResult<FaceMatch>.Rejected(code),
        ForcedScore = BiometryResult<ConfidenceScore>.Rejected(code),
    };

    /// <summary>Forces every endpoint to fail technically — "we learned nothing, replay later".</summary>
    public static FakeBiometryClient Unavailable(string code = BiometryCodes.ServiceUnavailable) => new()
    {
        ForcedDocumentReading = BiometryResult<OcrReading>.Unavailable(code),
        ForcedFaceMatch = BiometryResult<FaceMatch>.Unavailable(code),
        ForcedScore = BiometryResult<ConfidenceScore>.Unavailable(code),
    };

    // ── Forced outcomes: null means "answer the plausible success below" ──────────────────────

    public BiometryResult<OcrReading>? ForcedDocumentReading { get; set; }

    public BiometryResult<FaceMatch>? ForcedFaceMatch { get; set; }

    public BiometryResult<ConfidenceScore>? ForcedScore { get; set; }

    // ── Knobs on the plausible success ───────────────────────────────────────────────────────

    public string DocumentType { get; set; } = "CNI";

    /// <summary>
    /// Mutable so a test can add or overwrite one field without restating the document. The
    /// defaults are an Ivorian national id card, the commonest case in production.
    /// </summary>
    public Dictionary<string, string> Fields { get; } = new()
    {
        ["surname"] = "OUATTARA",
        ["given_names"] = "AWA",
        ["document_number"] = "CI0012345678",
        ["date_of_birth"] = "1987-04-02",
        ["expiry_date"] = "2030-01-31",
        ["nationality"] = "CIV",
        ["sex"] = "F",
    };

    /// <summary>Confidence per field; any field absent from here is reported at 0.97.</summary>
    public Dictionary<string, double> FieldConfidences { get; } = [];

    /// <summary>Set to false to exercise the MRZ-checksum path without failing the call.</summary>
    public bool MrzChecksumValid { get; set; } = true;

    /// <summary>Set to null to answer a document that has no machine-readable zone.</summary>
    public string? MrzRaw { get; set; } =
        "I<CIVCI0012345678<<<<<<<<<<<<<<\n8704029F3001318CIV<<<<<<<<<<<4\nOUATTARA<<AWA<<<<<<<<<<<<<<<<<";

    public double Similarity { get; set; } = 0.93;

    /// <summary>
    /// Null — the default — derives the verdict from <see cref="Similarity"/>, so lowering the
    /// similarity in a test does not leave a "no match at 0.2 similarity" answer behind. Set it to
    /// pin the two apart on purpose.
    /// </summary>
    public bool? IsMatch { get; set; }

    public double PortraitQuality { get; set; } = 0.88;

    public double SelfieQuality { get; set; } = 0.91;

    public int Score { get; set; } = 82;

    public List<string> Flags { get; } = [];

    // ── What the double recorded ─────────────────────────────────────────────────────────────

    public List<BiometryCall> Calls { get; } = [];

    public BiometryImage? LastDocumentImage { get; private set; }

    public BiometryImage? LastDocumentPortrait { get; private set; }

    public BiometryImage? LastSelfie { get; private set; }

    public ScoreRequest? LastScoreRequest { get; private set; }

    public Task<BiometryResult<OcrReading>> ReadDocumentAsync(
        Guid tenantId, BiometryImage image, string correlationId, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        Calls.Add(new BiometryCall("ocr", tenantId, correlationId));
        LastDocumentImage = image;

        return Task.FromResult(ForcedDocumentReading ?? BiometryResult<OcrReading>.Success(
            new OcrReading(
                DocumentType,
                new Dictionary<string, string>(Fields),
                Fields.Keys.ToDictionary(
                    key => key,
                    key => FieldConfidences.TryGetValue(key, out var c) ? c : 0.97),
                MrzRaw is null
                    ? null
                    : new MrzReading(MrzRaw, MrzChecksumValid, new Dictionary<string, string>(Fields)),
                ServiceVersion)));
    }

    public Task<BiometryResult<FaceMatch>> MatchFaceAsync(
        Guid tenantId, BiometryImage documentPortrait, BiometryImage selfie,
        string correlationId, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        Calls.Add(new BiometryCall("face-match", tenantId, correlationId));
        LastDocumentPortrait = documentPortrait;
        LastSelfie = selfie;

        return Task.FromResult(ForcedFaceMatch ?? BiometryResult<FaceMatch>.Success(
            new FaceMatch(
                Similarity,
                IsMatch ?? Similarity >= DefaultMatchThreshold,
                PortraitQuality,
                SelfieQuality,
                FaceModelVersion,
                ServiceVersion)));
    }

    public Task<BiometryResult<ConfidenceScore>> ScoreAsync(
        Guid tenantId, ScoreRequest request, string correlationId, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        Calls.Add(new BiometryCall("score", tenantId, correlationId));
        LastScoreRequest = request;

        // The breakdown is derived from Score rather than fixed, so moving the score in a test
        // never produces criteria that contradict the total a reader would check them against.
        var document = Score * 40 / 100;
        var face = Score * 35 / 100;

        return Task.FromResult(ForcedScore ?? BiometryResult<ConfidenceScore>.Success(
            new ConfidenceScore(
                Score,
                BiometryConfidenceLevels.FromScore(Score),
                new Dictionary<string, int>
                {
                    ["document"] = document,
                    ["face"] = face,
                    ["consistency"] = Score - document - face,
                },
                [.. Flags],
                ServiceVersion)));
    }
}

/// <summary>One call the fake answered, so a test can assert on who asked and under which id.</summary>
internal sealed record BiometryCall(string Endpoint, Guid TenantId, string CorrelationId);
