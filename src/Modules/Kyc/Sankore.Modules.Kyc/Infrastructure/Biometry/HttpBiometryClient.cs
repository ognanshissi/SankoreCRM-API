namespace Sankore.Modules.Kyc.Infrastructure.Biometry;

using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Sankore.Modules.Kyc.Domain;
using Sankore.Modules.Kyc.Infrastructure.Biometry.Generated;
using Sankore.Shared.Kernel;

/// <summary>
/// Talks to the external Flask biometric service through the client GENERATED from its own OpenAPI
/// document (<c>Infrastructure/Biometry/biometry-openapi.json</c>, regenerated on every build).
///
/// <para>
/// The wire shapes used to be hand-written records here, and not one of them matched the document:
/// the service reports <c>model_versions.service</c> and the mappers below demanded
/// <c>service_version</c>, so every single call would have mapped to null and been reported as
/// <c>BIOMETRY_UNEXPECTED_RESPONSE</c> — every file stuck in Verifying, retried three times, for a
/// service that was answering perfectly. Generating the DTOs is what makes that class of mismatch
/// a build error instead of a runtime outage.
/// </para>
///
/// <para>
/// What is NOT generated, and stays here, is the policy: the per-tenant token, the per-endpoint
/// timeout, the size cap, and above all the classification of a failure into the three outcomes of
/// <see cref="BiometryOutcome"/>. Nothing in this class throws for a service failure; the only
/// exception that leaves it is the caller's own cancellation, which is not a service outcome.
/// </para>
/// </summary>
internal sealed class HttpBiometryClient(
    IHttpClientFactory httpClientFactory,
    ISecretsModule secrets,
    IOptions<BiometryOptions> options,
    ILogger<HttpBiometryClient> logger) : IBiometryClient
{
    /// <summary>Named client so the host owns the handlers, the proxy and the retry budget.</summary>
    public const string HttpClientName = "kyc-biometry";

    /// <summary>
    /// The only status that means "I worked, your input is unusable" — see
    /// <see cref="ClassifyFailure{T}"/>. 413 joins it: an image the service refuses as too large is
    /// a statement about the capture, and the agent can act on it.
    /// </summary>
    private const int FunctionalRejectionStatus = 422;
    private const int PayloadTooLargeStatus = 413;

    public Task<BiometryResult<OcrReading>> ReadDocumentAsync(
        Guid tenantId, BiometryImage image, KycDocumentType declaredType,
        string correlationId, CancellationToken ct) =>
        CallAsync<OcrResponse, OcrReading>(
            tenantId, "v1/ocr", options.Value.OcrTimeout, correlationId,
            (client, callCt) => client.V1OcrAsync(
                new OcrRequest
                {
                    Image_base64 = Convert.ToBase64String(image.Content),
                    Doc_type = ToWireDocType(declaredType),
                },
                callCt),
            MapOcr, ct);

    public Task<BiometryResult<FaceMatch>> MatchFaceAsync(
        Guid tenantId, BiometryImage documentPortrait, BiometryImage selfie,
        string correlationId, CancellationToken ct) =>
        CallAsync<FaceMatchResponse, FaceMatch>(
            tenantId, "v1/face-match", options.Value.FaceMatchTimeout, correlationId,
            (client, callCt) => client.V1FaceMatchAsync(
                new FaceMatchRequest
                {
                    Selfie_base64 = Convert.ToBase64String(selfie.Content),
                    // The full document image, not a cropped portrait: the service does its own
                    // face detection on it, which is why doc_face_base64 is the OTHER field and is
                    // left unset — we have no cropped face to send.
                    Doc_image_base64 = Convert.ToBase64String(documentPortrait.Content),
                },
                callCt),
            MapFaceMatch, ct);

    public Task<BiometryResult<ConfidenceScore>> ScoreAsync(
        Guid tenantId, ScoreRequest request, string correlationId, CancellationToken ct)
    {
        // The scorer is stateless and the contract says so explicitly: it is handed back the two
        // answers it is to weigh, verbatim. Rebuilding them from this module's projections would
        // have it grade a document whose anomalies, quality and MRZ checks had been dropped.
        if (request.Ocr?.Raw is null || request.FaceMatch?.Raw is null)
        {
            logger.LogError(
                "Scoring asked without the service's own OCR/face payloads | Correlation={CorrelationId}",
                correlationId);

            return Task.FromResult(BiometryResult<ConfidenceScore>.Unavailable(
                BiometryCodes.UnexpectedResponse,
                "The scorer must be given back the service's own OCR and face-match answers."));
        }

        var body = new Generated.ScoreRequest
        {
            Ocr_result = request.Ocr.Raw,
            Face_result = request.FaceMatch.Raw,
            Channel = ToWireChannel(request.Channel),
            Risk_level = ToWireRiskLevel(request.RiskLevel),
            Corrected_fields = request.CorrectedFieldNames?.ToList() ?? [],
        };

        return CallAsync<ScoreResponse, ConfidenceScore>(
            tenantId, "v1/score", options.Value.ScoreTimeout, correlationId,
            (client, callCt) => client.V1ScoreAsync(body, callCt),
            MapScore, ct);
    }

    /// <summary>
    /// One place for everything that is the same on the three calls: configuration, the tenant's
    /// token, the per-endpoint budget, and the translation of every failure mode into an outcome.
    /// </summary>
    /// <param name="call">
    /// Receives a client already carrying the token and the correlation id, plus the token of THIS
    /// endpoint's budget — not the caller's, so an expiry here is a technical result and not a
    /// cancellation.
    /// </param>
    private async Task<BiometryResult<T>> CallAsync<TWire, T>(
        Guid tenantId,
        string path,
        TimeSpan timeout,
        string correlationId,
        Func<BiometryGeneratedClient, CancellationToken, Task<TWire>> call,
        Func<TWire, T?> map,
        CancellationToken ct)
        where T : class
    {
        var opts = options.Value;

        var baseAddress = opts.ResolveBaseAddress();
        if (baseAddress is null)
        {
            return BiometryResult<T>.Unavailable(
                BiometryCodes.NotConfigured, $"{BiometryOptions.SectionName}:BaseUrl is not set.");
        }

        // Read before the client exists: a tenant with no token must not produce a single packet,
        // and an unauthenticated call would come back as a 401 that reads like a service outage.
        var token = await secrets.GetValueAsync(BiometrySecrets.TokenKey(tenantId), ct);
        if (string.IsNullOrWhiteSpace(token))
        {
            logger.LogWarning(
                "No biometry service token stored for tenant {TenantId}; skipping {Path}.",
                tenantId, path);

            return BiometryResult<T>.Unavailable(
                BiometryCodes.NotConfigured, "No biometry service token stored for this tenant.");
        }

        var httpClient = httpClientFactory.CreateClient(HttpClientName);

        // The generated client is a thin wrapper — the connection pool belongs to the HttpClient —
        // so one per call is what lets the token and the correlation id be per call instead of
        // living on a pooled client's DefaultRequestHeaders, where one tenant would inherit
        // another's token.
        var client = new BiometryGeneratedClient(baseAddress.ToString(), httpClient)
        {
            ServiceToken = token,
            CorrelationId = correlationId,
        };

        // Per-endpoint budget on a linked token rather than on HttpClient.Timeout: that is what
        // lets the catch below tell our own expiry (a technical result) from the caller giving up
        // (rethrown), which HttpClient.Timeout reports as the very same exception.
        using var attempt = CancellationTokenSource.CreateLinkedTokenSource(ct);
        attempt.CancelAfter(timeout);

        try
        {
            var wire = await call(client, attempt.Token);

            var mapped = wire is null ? null : map(wire);
            if (mapped is null)
            {
                logger.LogWarning(
                    "Biometry answer from {Path} is missing a mandatory field | Correlation={CorrelationId}",
                    path, correlationId);

                return BiometryResult<T>.Unavailable(BiometryCodes.UnexpectedResponse, null);
            }

            return BiometryResult<T>.Success(mapped);
        }
        catch (ApiException ex)
        {
            return ClassifyFailure<T>(ex, path, correlationId);
        }
        catch (BiometryResponseTooLargeException ex)
        {
            logger.LogWarning(
                "Biometry answer over the {Max}-byte cap on {Path} | Correlation={CorrelationId}",
                ex.MaxBytes, path, correlationId);

            return BiometryResult<T>.Unavailable(BiometryCodes.ResponseTooLarge, ex.Message);
        }
        catch (JsonException ex)
        {
            // A 200 we cannot read is still "we learned nothing", never a verdict on the photo.
            logger.LogWarning(
                ex, "Unreadable biometry answer from {Path} | Correlation={CorrelationId}",
                path, correlationId);

            return BiometryResult<T>.Unavailable(BiometryCodes.UnexpectedResponse, ex.Message);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            logger.LogWarning(
                "Biometry call to {Path} timed out after {Seconds}s | Correlation={CorrelationId}",
                path, timeout.TotalSeconds, correlationId);

            return BiometryResult<T>.Unavailable(
                BiometryCodes.Timeout, $"No answer from {path} within {timeout.TotalSeconds:0}s.");
        }
        catch (HttpRequestException ex)
        {
            logger.LogWarning(
                ex, "Biometry service unreachable on {Path} | Correlation={CorrelationId}",
                path, correlationId);

            return BiometryResult<T>.Unavailable(BiometryCodes.ServiceUnavailable, ex.Message);
        }
    }

    /// <summary>
    /// Turns the generated client's exception into one of the two failure outcomes.
    ///
    /// <para>
    /// Only 422 and 413 are functional, because they are the two statuses that say "I ran, and the
    /// input you gave me cannot be used". Every other status says nothing about the input: a 401 is
    /// our token, a 404 our URL, a 503 its models, a 500 its bug. Reporting any of those as a
    /// rejection would ask a client for a new photo because of our own outage — and, worse, record
    /// a rejection on a file that was never examined.
    /// </para>
    ///
    /// <para>
    /// The code is read from the service's own error envelope (<c>{"error":{"code":…}}</c>), which
    /// the previous version of this class could not see: it looked for a STRING at the root of the
    /// body, so a 422 carrying a perfectly good rejection reason degraded to a technical result
    /// and the agent was never told what to re-shoot.
    /// </para>
    /// </summary>
    private BiometryResult<T> ClassifyFailure<T>(ApiException ex, string path, string correlationId)
        where T : class
    {
        var code = TryExtractCode(ex);
        var detail = Describe(ex);

        // A 2xx the generated client could not deserialise: it reports that as an ApiException too,
        // with the SUCCESS status still on it. It is "we learned nothing", never a verdict on the
        // photo — and not an outage either, which is why it keeps its own code.
        if (ex.StatusCode is >= 200 and < 300)
        {
            logger.LogWarning(
                ex, "Unreadable biometry answer from {Path} | Correlation={CorrelationId}",
                path, correlationId);

            return BiometryResult<T>.Unavailable(BiometryCodes.UnexpectedResponse, detail);
        }

        if (ex.StatusCode is FunctionalRejectionStatus or PayloadTooLargeStatus)
        {
            if (code is null)
            {
                // A rejection whose body we cannot read carries no verdict to record, so it
                // degrades to technical rather than to an invented reason.
                logger.LogWarning(
                    "Biometry {Status} from {Path} without a service code | Correlation={CorrelationId}",
                    ex.StatusCode, path, correlationId);

                return BiometryResult<T>.Unavailable(BiometryCodes.UnexpectedResponse, detail);
            }

            return BiometryResult<T>.Rejected(code, detail);
        }

        logger.LogWarning(
            "Biometry {Path} answered HTTP {Status} ({Code}) | Correlation={CorrelationId}",
            path, ex.StatusCode, code ?? "-", correlationId);

        return BiometryResult<T>.Unavailable(
            code ?? BiometryCodes.ServiceUnavailable, $"HTTP {ex.StatusCode}: {detail}");
    }

    /// <summary>
    /// The service's error code, from the typed envelope when the generated client managed to
    /// deserialise it, and from the raw body otherwise — a proxy's HTML page or a bare token
    /// (<c>MODELS_NOT_READY</c>) never reaches the typed path.
    /// </summary>
    private static string? TryExtractCode(ApiException ex)
    {
        if (ex is ApiException<ErrorResponse> typed && IsCodeToken(typed.Result?.Error?.Code))
            return typed.Result!.Error?.Code;

        var text = ex.Response?.Trim();
        if (string.IsNullOrEmpty(text))
            return null;

        if (text.StartsWith('{'))
        {
            try
            {
                using var doc = JsonDocument.Parse(text);
                if (doc.RootElement.ValueKind != JsonValueKind.Object)
                    return null;

                // The service's envelope nests it; the bare forms are kept for a gateway that
                // rewrites the body into something flatter.
                if (doc.RootElement.TryGetProperty("error", out var error)
                    && error.ValueKind == JsonValueKind.Object
                    && error.TryGetProperty("code", out var nested)
                    && nested.ValueKind == JsonValueKind.String
                    && IsCodeToken(nested.GetString()))
                {
                    return nested.GetString();
                }

                string[] candidates = ["error_code", "code", "error"];

                foreach (var name in candidates)
                {
                    if (doc.RootElement.TryGetProperty(name, out var el)
                        && el.ValueKind == JsonValueKind.String
                        && IsCodeToken(el.GetString()))
                    {
                        return el.GetString();
                    }
                }
            }
            catch (JsonException)
            {
                return null;
            }

            return null;
        }

        return IsCodeToken(text) ? text : null;
    }

    /// <summary>
    /// What to put on the audit row. The generated client EMPTIES <c>Response</c> once it has
    /// deserialised a declared error type, so the service's own message has to be read off the
    /// typed payload — otherwise the detail of every 422 is a blank string, and the one sentence
    /// explaining the rejection to whoever reads the file later is lost.
    /// </summary>
    private static string? Describe(ApiException ex)
    {
        if (ex is ApiException<ErrorResponse> { Result.Error: { } error })
        {
            return string.IsNullOrWhiteSpace(error.Message)
                ? error.Code
                : $"{error.Code}: {error.Message}";
        }

        return Excerpt(ex.Response);
    }

    /// <summary>UPPER_SNAKE and nothing else, so prose never ends up stored as an error code.</summary>
    private static bool IsCodeToken(string? value) =>
        value is { Length: >= 3 and <= 64 }
        && char.IsAsciiLetterUpper(value[0])
        && value.All(c => char.IsAsciiLetterUpper(c) || char.IsAsciiDigit(c) || c == '_');

    /// <summary>
    /// Truncated: this ends up in a log line and on an audit row, and the body may be a whole HTML
    /// page.
    /// </summary>
    private static string? Excerpt(string? body) =>
        body is null ? null : body.Length <= 500 ? body : body[..500];

    // ── Domain → wire ────────────────────────────────────────────────────────────────────────

    private static DocType ToWireDocType(KycDocumentType declared) => declared switch
    {
        KycDocumentType.Passport => DocType.PASSPORT,
        KycDocumentType.Cedeao => DocType.CEDEAO,
        KycDocumentType.Consulaire => DocType.CONSULAIRE,
        _ => DocType.CNI,
    };

    /// <summary>
    /// M02's five channels onto the service's three. Import and LeadConversion are REMOTE because
    /// that is what they are from the scorer's point of view: nobody was in front of the customer.
    /// </summary>
    private static Channel ToWireChannel(KycChannel channel) => channel switch
    {
        KycChannel.Agency => Channel.BRANCH,
        KycChannel.MobileAgent => Channel.FIELD_AGENT,
        _ => Channel.REMOTE,
    };

    private static RiskLevel ToWireRiskLevel(KycVigilanceLevel level) => level switch
    {
        KycVigilanceLevel.High => RiskLevel.HIGH,
        KycVigilanceLevel.Low => RiskLevel.LOW,
        _ => RiskLevel.STANDARD,
    };

    // ── Wire → domain ────────────────────────────────────────────────────────────────────────
    //
    // Each mapper returns null when a field the business cannot do without is absent, which the
    // caller turns into a technical result. ServiceVersion is one of them: a verification is kept
    // as evidence and must stay attributable to the model that produced it, so a reading we cannot
    // attribute is worth less than no reading at all.

    private static OcrReading? MapOcr(OcrResponse wire)
    {
        var serviceVersion = wire.Model_versions?.Service;
        if (string.IsNullOrWhiteSpace(serviceVersion))
            return null;

        // The service reports one object per field (value + confidence + source). Split into the
        // two flat maps the module stores, dropping fields it read as empty — a null value is "not
        // found on the document", and keeping it would make an absent field look extracted.
        var fields = new Dictionary<string, string>(StringComparer.Ordinal);
        var confidences = new Dictionary<string, double>(StringComparer.Ordinal);

        foreach (var (name, field) in wire.Fields ?? new Dictionary<string, FieldValue>())
        {
            if (field is null || string.IsNullOrWhiteSpace(field.Value))
                continue;

            fields[name] = field.Value;
            confidences[name] = field.Confidence;
        }

        return new OcrReading(
            DocumentType: wire.Doc_type.ToString(),
            Fields: fields,
            FieldConfidences: confidences,
            Mrz: MapMrz(wire.Mrz),
            ServiceVersion: serviceVersion,
            Raw: wire);
    }

    /// <summary>
    /// The MRZ as parsed fields, never as its raw line — <c>MrzReading.Raw</c> stays null on
    /// purpose, and the document says the service does not return one either. That line IS the
    /// document number spelled out, and the number is the one value the evidence table encrypts.
    /// </summary>
    private static MrzReading? MapMrz(MrzData? mrz)
    {
        if (mrz is null)
            return null;

        var fields = new Dictionary<string, string>(StringComparer.Ordinal);

        void Add(string key, string? value)
        {
            if (!string.IsNullOrWhiteSpace(value)) fields[key] = value;
        }

        Add("format", mrz.Format);
        Add("document_code", mrz.Document_code);
        Add("document_number", mrz.Document_number);
        Add("issuing_country", mrz.Issuing_country);
        Add("nationality", mrz.Nationality);
        Add("last_name", mrz.Last_name);
        Add("first_name", mrz.First_name);
        Add("sex", mrz.Sex);
        Add("birth_date", mrz.Birth_date);
        Add("expiry_date", mrz.Expiry_date);

        return new MrzReading(Raw: null, ChecksumValid: mrz.Checksums_valid, Fields: fields);
    }

    private static FaceMatch? MapFaceMatch(FaceMatchResponse wire)
    {
        var serviceVersion = wire.Model_versions?.Service;
        if (string.IsNullOrWhiteSpace(serviceVersion))
            return null;

        return new FaceMatch(
            Similarity: wire.Similarity_score,
            IsMatch: wire.Is_match,
            PortraitQuality: wire.Quality?.Document?.Score ?? 0,
            SelfieQuality: wire.Quality?.Selfie?.Score ?? 0,
            ModelVersion: serviceVersion,
            ServiceVersion: serviceVersion,
            Threshold: wire.Threshold,
            Raw: wire);
    }

    private static ConfidenceScore? MapScore(ScoreResponse wire)
    {
        var serviceVersion = wire.Model_versions?.Service;
        if (string.IsNullOrWhiteSpace(serviceVersion))
            return null;

        var score = Math.Clamp(wire.Global_score, 0, 100);

        // The breakdown is one object per criterion; the module stores whole points per criterion,
        // which is what the verification panel draws. Rounded away from zero so a criterion worth
        // 0.5 point does not display as contributing nothing.
        var breakdown = new Dictionary<string, int>(StringComparer.Ordinal);

        foreach (var (name, component) in wire.Breakdown ?? new Dictionary<string, ScoreComponent>())
        {
            if (component is null) continue;
            breakdown[name] = (int)Math.Round(component.Points, MidpointRounding.AwayFromZero);
        }

        return new ConfidenceScore(
            Score: score,
            Level: MapLevel(wire.Level, score),
            Breakdown: breakdown,
            // Codes only: that is what the module stores and what the front translates. The
            // severity and the blocking flag are the service's own weighting, already reflected in
            // the score it just gave us.
            Flags: (wire.Flags ?? []).Where(f => f?.Code is not null).Select(f => f.Code).ToList(),
            ServiceVersion: serviceVersion);
    }

    /// <summary>
    /// The service's three grades onto this module's three.
    ///
    /// <para>
    /// Its vocabulary is VALIDATED / REVIEW_REQUIRED / REJECTED, and the previous version parsed it
    /// as Low/Medium/High — which never matched, so the level was silently replaced by our own
    /// thresholds on every call. REJECTED maps to <see cref="BiometryConfidenceLevel.Low"/> and NOT
    /// to a refusal: refusing a file stays M02's decision, taken in
    /// <c>RunKycVerificationHandler</c> from the face verdict and the tenant's rejection floor, and
    /// a model does not get to close a customer's file on its own.
    /// </para>
    /// </summary>
    private static BiometryConfidenceLevel MapLevel(ConfidenceLevel level, int score) => level switch
    {
        ConfidenceLevel.VALIDATED => BiometryConfidenceLevel.High,
        ConfidenceLevel.REVIEW_REQUIRED => BiometryConfidenceLevel.Medium,
        ConfidenceLevel.REJECTED => BiometryConfidenceLevel.Low,
        _ => BiometryConfidenceLevels.FromScore(score),
    };
}
