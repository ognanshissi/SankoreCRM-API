namespace Sankore.Modules.Kyc.Infrastructure.Biometry;

using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Sankore.Shared.Kernel;

/// <summary>
/// Talks to the external Flask biometric service over HTTP/JSON.
///
/// Images travel base64-encoded inside the JSON body rather than as multipart: the three endpoints
/// then share one request shape, one size cap and one error envelope, and the payload is something
/// a test can assert on. The extra third of a transfer is irrelevant next to the model run.
///
/// Nothing here throws for a service failure — see <see cref="BiometryOutcome"/> for why the
/// functional / technical split is load-bearing. The only exception that leaves this class is the
/// caller's own cancellation, which is not a service outcome at all.
/// </summary>
internal sealed class HttpBiometryClient(
    IHttpClientFactory httpClientFactory,
    ISecretsModule secrets,
    IOptions<BiometryOptions> options,
    ILogger<HttpBiometryClient> logger) : IBiometryClient
{
    /// <summary>Named client so the host owns the handlers, the proxy and the retry budget.</summary>
    public const string HttpClientName = "kyc-biometry";

    private const string OcrPath = "v1/ocr";
    private const string FaceMatchPath = "v1/face-match";
    private const string ScorePath = "v1/score";

    /// <summary>Echoed by the service into its own logs; the two deployments correlate on it.</summary>
    private const string CorrelationHeader = "X-Correlation-Id";

    /// <summary>
    /// The only status that means "I worked, your input is unusable". See
    /// <see cref="ClassifyFailure{T}"/>.
    /// </summary>
    private const int FunctionalRejectionStatus = 422;

    private static readonly IReadOnlyDictionary<string, string> EmptyStrings =
        new Dictionary<string, string>();

    private static readonly IReadOnlyDictionary<string, double> EmptyNumbers =
        new Dictionary<string, double>();

    private static readonly IReadOnlyDictionary<string, int> EmptyCounts =
        new Dictionary<string, int>();

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        // The Flask service speaks snake_case. DictionaryKeyPolicy is deliberately NOT set:
        // dictionary keys are the service's own OCR field names and must travel verbatim —
        // renaming them would silently rename every extracted field.
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public Task<BiometryResult<OcrReading>> ReadDocumentAsync(
        Guid tenantId, BiometryImage image, string correlationId, CancellationToken ct) =>
        PostAsync<OcrResponse, OcrReading>(
            tenantId, OcrPath, new OcrRequestBody(ImageBody.From(image)),
            options.Value.OcrTimeout, correlationId, MapOcr, ct);

    public Task<BiometryResult<FaceMatch>> MatchFaceAsync(
        Guid tenantId, BiometryImage documentPortrait, BiometryImage selfie,
        string correlationId, CancellationToken ct) =>
        PostAsync<FaceMatchResponse, FaceMatch>(
            tenantId, FaceMatchPath,
            new FaceMatchRequestBody(ImageBody.From(documentPortrait), ImageBody.From(selfie)),
            options.Value.FaceMatchTimeout, correlationId, MapFaceMatch, ct);

    public Task<BiometryResult<ConfidenceScore>> ScoreAsync(
        Guid tenantId, ScoreRequest request, string correlationId, CancellationToken ct) =>
        // The scorer is stateless, so it is handed back the readings it is to weigh; the record is
        // serialised as-is, which keeps the wire shape and the contract impossible to desynchronise.
        PostAsync<ScoreResponse, ConfidenceScore>(
            tenantId, ScorePath, request,
            options.Value.ScoreTimeout, correlationId, MapScore, ct);

    private async Task<BiometryResult<T>> PostAsync<TWire, T>(
        Guid tenantId,
        string path,
        object payload,
        TimeSpan timeout,
        string correlationId,
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

        var client = httpClientFactory.CreateClient(HttpClientName);

        // ??= so a host that configured the named client keeps its own address.
        client.BaseAddress ??= baseAddress;

        // Per-endpoint budget enforced on a linked token rather than on HttpClient.Timeout: that is
        // what lets the catch below tell our own expiry (technical result) from the caller giving up
        // (rethrown), which HttpClient.Timeout reports as the very same TaskCanceledException.
        using var attempt = CancellationTokenSource.CreateLinkedTokenSource(ct);
        attempt.CancelAfter(timeout);

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, path)
            {
                Content = JsonContent.Create(payload, payload.GetType(), options: JsonOpts),
            };

            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            request.Headers.TryAddWithoutValidation(CorrelationHeader, correlationId);

            // ResponseHeadersRead, so the size guard below runs before the body is in memory.
            using var response = await client.SendAsync(
                request, HttpCompletionOption.ResponseHeadersRead, attempt.Token);

            var body = await ReadBoundedAsync(response.Content, opts.MaxResponseBytes, attempt.Token);
            if (body is null)
            {
                logger.LogWarning(
                    "Biometry answer over the {Max}-byte cap on {Path} | Correlation={CorrelationId}",
                    opts.MaxResponseBytes, path, correlationId);

                return BiometryResult<T>.Unavailable(
                    BiometryCodes.ResponseTooLarge,
                    $"{path} answered more than {opts.MaxResponseBytes} bytes.");
            }

            if (!response.IsSuccessStatusCode)
                return ClassifyFailure<T>((int)response.StatusCode, body, path, correlationId);

            TWire? wire;
            try
            {
                wire = JsonSerializer.Deserialize<TWire>(body, JsonOpts);
            }
            catch (JsonException ex)
            {
                // A 200 we cannot read is still "we learned nothing", never a verdict on the photo.
                logger.LogWarning(
                    ex, "Unreadable biometry answer from {Path} | Correlation={CorrelationId}",
                    path, correlationId);

                return BiometryResult<T>.Unavailable(BiometryCodes.UnexpectedResponse, Excerpt(body));
            }

            var mapped = wire is null ? null : map(wire);
            if (mapped is null)
            {
                logger.LogWarning(
                    "Biometry answer from {Path} is missing a mandatory field | Correlation={CorrelationId}",
                    path, correlationId);

                return BiometryResult<T>.Unavailable(BiometryCodes.UnexpectedResponse, Excerpt(body));
            }

            return BiometryResult<T>.Success(mapped);
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
    /// Turns a non-2xx answer into one of the two failure outcomes.
    ///
    /// Only 422 is functional, because it is the one status the service uses to say "I ran, and the
    /// input you gave me cannot be used". Every other status says nothing about the input: a 401 is
    /// our token, a 404 our URL, a 503 its models, a 500 its bug. Reporting any of those as a
    /// rejection would ask a client for a new photo because of our own outage — and, worse, record
    /// a rejection on a file that was never examined.
    /// </summary>
    private BiometryResult<T> ClassifyFailure<T>(int status, byte[] body, string path, string correlationId)
        where T : class
    {
        var code = TryExtractCode(body);

        if (status == FunctionalRejectionStatus)
        {
            if (code is null)
            {
                // A 422 whose body we cannot read carries no verdict to record, so it degrades to
                // technical rather than to an invented rejection reason.
                logger.LogWarning(
                    "Biometry 422 from {Path} without a service code | Correlation={CorrelationId}",
                    path, correlationId);

                return BiometryResult<T>.Unavailable(BiometryCodes.UnexpectedResponse, Excerpt(body));
            }

            return BiometryResult<T>.Rejected(code, Excerpt(body));
        }

        logger.LogWarning(
            "Biometry {Path} answered HTTP {Status} ({Code}) | Correlation={CorrelationId}",
            path, status, code ?? "-", correlationId);

        return BiometryResult<T>.Unavailable(
            code ?? BiometryCodes.ServiceUnavailable, $"HTTP {status}: {Excerpt(body)}");
    }

    /// <summary>
    /// Reads at most <paramref name="max"/> + 1 bytes and returns null past the cap, so an oversized
    /// answer is refused without ever being fully buffered. The declared length is honoured first
    /// when the service sends one, which costs nothing and avoids opening the stream at all.
    /// </summary>
    private static async Task<byte[]?> ReadBoundedAsync(HttpContent content, long max, CancellationToken ct)
    {
        if (content.Headers.ContentLength > max)
            return null;

        await using var stream = await content.ReadAsStreamAsync(ct);

        var buffer = new byte[8192];
        using var buffered = new MemoryStream();

        while (true)
        {
            // One byte past the cap is all it takes to know we are over it.
            var room = max + 1 - buffered.Length;
            if (room <= 0)
                return null;

            var read = await stream.ReadAsync(
                buffer.AsMemory(0, (int)Math.Min(buffer.Length, room)), ct);

            if (read == 0)
                break;

            buffered.Write(buffer, 0, read);

            if (buffered.Length > max)
                return null;
        }

        return buffered.ToArray();
    }

    /// <summary>
    /// Pulls the service's error code out of an error body, accepting both forms it uses: a JSON
    /// envelope (<c>{"error_code":"NO_FACE_DETECTED"}</c>) and the bare token a 503 returns
    /// (<c>MODELS_NOT_READY</c>). Returns null for anything else — including an HTML proxy page,
    /// which must not become a business reason.
    /// </summary>
    private static string? TryExtractCode(byte[] body)
    {
        var text = Encoding.UTF8.GetString(body).Trim();
        if (text.Length == 0)
            return null;

        if (text.StartsWith('{'))
        {
            try
            {
                using var doc = JsonDocument.Parse(text);
                if (doc.RootElement.ValueKind != JsonValueKind.Object)
                    return null;

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

    /// <summary>UPPER_SNAKE and nothing else, so prose never ends up stored as an error code.</summary>
    private static bool IsCodeToken(string? value) =>
        value is { Length: >= 3 and <= 64 }
        && char.IsAsciiLetterUpper(value[0])
        && value.All(c => char.IsAsciiLetterUpper(c) || char.IsAsciiDigit(c) || c == '_');

    /// <summary>
    /// Truncated: this ends up in a log line and on an audit row, and the body may be a whole HTML
    /// page. Decoded before truncating so a cut never lands inside a multi-byte character.
    /// </summary>
    private static string Excerpt(byte[] body)
    {
        var text = Encoding.UTF8.GetString(body);
        return text.Length <= 500 ? text : text[..500];
    }

    // ── Mapping ──────────────────────────────────────────────────────────────────────────────
    //
    // Each mapper returns null when a field the business cannot do without is absent, which the
    // caller turns into a technical result. ServiceVersion is one of them: a verification is kept
    // as evidence and must stay attributable to the model that produced it, so a reading we cannot
    // attribute is worth less than no reading at all.

    private static OcrReading? MapOcr(OcrResponse wire)
    {
        if (string.IsNullOrWhiteSpace(wire.ServiceVersion))
            return null;

        return new OcrReading(
            DocumentType: string.IsNullOrWhiteSpace(wire.DocumentType) ? "UNKNOWN" : wire.DocumentType,
            Fields: wire.Fields ?? EmptyStrings,
            FieldConfidences: wire.FieldConfidences ?? EmptyNumbers,
            Mrz: wire.Mrz is null
                ? null
                : new MrzReading(wire.Mrz.Raw, wire.Mrz.ChecksumValid ?? false, wire.Mrz.Fields ?? EmptyStrings),
            ServiceVersion: wire.ServiceVersion);
    }

    private static FaceMatch? MapFaceMatch(FaceMatchResponse wire)
    {
        if (string.IsNullOrWhiteSpace(wire.ServiceVersion) || wire.Similarity is null || wire.IsMatch is null)
            return null;

        return new FaceMatch(
            Similarity: wire.Similarity.Value,
            IsMatch: wire.IsMatch.Value,
            PortraitQuality: wire.PortraitQuality ?? 0,
            SelfieQuality: wire.SelfieQuality ?? 0,
            ModelVersion: string.IsNullOrWhiteSpace(wire.ModelVersion) ? "unknown" : wire.ModelVersion,
            ServiceVersion: wire.ServiceVersion);
    }

    private static ConfidenceScore? MapScore(ScoreResponse wire)
    {
        if (string.IsNullOrWhiteSpace(wire.ServiceVersion) || wire.Score is null)
            return null;

        var score = Math.Clamp(wire.Score.Value, 0, 100);

        // The score is authoritative and the service's level vocabulary may grow; an unknown label
        // falls back to our own thresholds rather than inventing a fourth level the module cannot
        // handle.
        var level = Enum.TryParse<BiometryConfidenceLevel>(wire.Level, ignoreCase: true, out var parsed)
            ? parsed
            : BiometryConfidenceLevels.FromScore(score);

        return new ConfidenceScore(
            Score: score,
            Level: level,
            Breakdown: wire.Breakdown ?? EmptyCounts,
            Flags: wire.Flags ?? [],
            ServiceVersion: wire.ServiceVersion);
    }

    // ── Wire shapes ──────────────────────────────────────────────────────────────────────────

    private sealed record ImageBody(string Content, string ContentType, string? FileName)
    {
        public static ImageBody From(BiometryImage image) =>
            new(Convert.ToBase64String(image.Content), image.ContentType, image.FileName);
    }

    private sealed record OcrRequestBody(ImageBody Image);

    private sealed record FaceMatchRequestBody(ImageBody DocumentPortrait, ImageBody Selfie);

    // Every field is nullable: a missing one is the service's answer being incomplete, not a
    // deserialisation crash. The mappers above decide which absences are fatal.
    private sealed record OcrResponse(
        string? DocumentType,
        Dictionary<string, string>? Fields,
        Dictionary<string, double>? FieldConfidences,
        MrzResponse? Mrz,
        string? ServiceVersion);

    private sealed record MrzResponse(
        string? Raw,
        bool? ChecksumValid,
        Dictionary<string, string>? Fields);

    private sealed record FaceMatchResponse(
        double? Similarity,
        bool? IsMatch,
        double? PortraitQuality,
        double? SelfieQuality,
        string? ModelVersion,
        string? ServiceVersion);

    private sealed record ScoreResponse(
        int? Score,
        string? Level,
        Dictionary<string, int>? Breakdown,
        List<string>? Flags,
        string? ServiceVersion);
}
