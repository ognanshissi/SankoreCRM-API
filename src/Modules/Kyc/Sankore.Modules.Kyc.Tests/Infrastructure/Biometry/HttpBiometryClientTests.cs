namespace Sankore.Modules.Kyc.Tests.Infrastructure.Biometry;

using System.Net;
using System.Text;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Sankore.Modules.Kyc.Domain;
using Sankore.Modules.Kyc.Infrastructure.Biometry;
using Sankore.Shared.Kernel;
using Xunit;

/// <summary>
/// The client over the GENERATED biometry client.
///
/// <para>
/// Every body below is the service's own shape, taken from <c>biometry-openapi.json</c>, and that
/// is the point of the exercise: the fixtures this file used to carry (<c>service_version</c>,
/// <c>similarity</c>, <c>document_type</c>, a flat <c>fields</c> map) matched the hand-written
/// records and NOTHING the service actually sends. They passed while the client could not have
/// read a single real answer.
/// </para>
/// </summary>
public sealed class HttpBiometryClientTests
{
    private static readonly Guid Tenant = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private const string Correlation = "kyc-verification-42";
    private const string ServiceVersion = "biometry-2.4.1";

    /// <summary>
    /// A field whose <c>value</c> is null is the service saying "not found on this document", so
    /// <c>place_of_birth</c> is here to prove it is dropped rather than stored as an empty reading.
    /// </summary>
    private const string OcrBody = """
        {
          "doc_type": "CNI",
          "fields": {
            "surname":        { "value": "OUATTARA", "confidence": 0.98, "source": "VISUAL" },
            "given_names":    { "value": "AWA",      "confidence": 0.91, "source": "MRZ" },
            "place_of_birth": { "value": null,       "confidence": 0.0,  "source": "VISUAL" }
          },
          "mrz": {
            "format": "TD1",
            "checksums_valid": false,
            "checks": [ { "field": "document_number", "valid": false } ],
            "document_code": "ID",
            "issuing_country": "CIV",
            "document_number": "CI0012345678",
            "birth_date": "1987-04-02",
            "sex": "F",
            "expiry_date": "2030-01-01",
            "nationality": "CIV",
            "last_name": "OUATTARA",
            "first_name": "AWA"
          },
          "anomalies": [ { "code": "EXPIRED_DOCUMENT", "severity": "MAJOR" } ],
          "quality": { "score": 0.82, "preprocessed": true },
          "model_versions": { "service": "biometry-2.4.1" }
        }
        """;

    private const string FaceMatchBody = """
        {
          "similarity_score": 0.91,
          "is_match": true,
          "threshold": 0.62,
          "detection_scores": { "selfie": 0.99 },
          "quality": {
            "selfie":   { "score": 0.77, "preprocessed": false },
            "document": { "score": 0.80, "preprocessed": true }
          },
          "model_versions": { "service": "biometry-2.4.1" }
        }
        """;

    private const string ScoreBody = """
        {
          "global_score": 73,
          "level": "REVIEW_REQUIRED",
          "breakdown": {
            "document": { "ratio": 0.75, "weight": 40, "points": 30 },
            "face":     { "ratio": 0.81, "weight": 35, "points": 28.4 }
          },
          "penalties": 5,
          "flags": [ { "code": "NAME_MISMATCH", "severity": "MINOR", "blocking": false } ],
          "model_versions": { "service": "biometry-2.4.1" }
        }
        """;

    /// <summary>Captures the outgoing request and answers with whatever the test wants.</summary>
    private sealed class CapturingHandler : HttpMessageHandler
    {
        private readonly Func<CancellationToken, Task<HttpResponseMessage>> _respond;

        public CapturingHandler(HttpStatusCode status, string body)
            : this(_ => Task.FromResult(new HttpResponseMessage(status)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            }))
        {
        }

        public CapturingHandler(Func<CancellationToken, Task<HttpResponseMessage>> respond) =>
            _respond = respond;

        public HttpRequestMessage? Request { get; private set; }

        public string? Payload { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken ct)
        {
            Request = request;
            Payload = request.Content is null ? null : await request.Content.ReadAsStringAsync(ct);

            return await _respond(ct);
        }
    }

    private static BiometryImage Image(params byte[] content) => new(content, "image/jpeg");

    private static (HttpBiometryClient Client, CapturingHandler Handler, ISecretsModule Secrets) Build(
        CapturingHandler handler,
        string? token = "biometry-token",
        Action<BiometryOptions>? configure = null)
    {
        var options = new BiometryOptions { BaseUrl = "https://biometry.internal" };
        configure?.Invoke(options);

        var secrets = Substitute.For<ISecretsModule>();
        secrets.GetValueAsync(Arg.Any<SecretKey>(), Arg.Any<CancellationToken>()).Returns(token);

        // The size guard sits in the pipeline exactly as the module registers it: the generated
        // client reads the whole body, so the cap can only live in a handler below it.
        var pipeline = new BoundedResponseHandler(Options.Create(options)) { InnerHandler = handler };

        var factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient(HttpBiometryClient.HttpClientName).Returns(_ => new HttpClient(pipeline));

        var client = new HttpBiometryClient(
            factory, secrets, Options.Create(options), NullLogger<HttpBiometryClient>.Instance);

        return (client, handler, secrets);
    }

    private static (HttpBiometryClient Client, CapturingHandler Handler, ISecretsModule Secrets) Build(
        HttpStatusCode status = HttpStatusCode.OK,
        string body = OcrBody,
        string? token = "biometry-token",
        Action<BiometryOptions>? configure = null) =>
        Build(new CapturingHandler(status, body), token, configure);

    /// <summary>A scorable request: the scorer is handed the service's own two answers back.</summary>
    private static async Task<ScoreRequest> ScorableAsync()
    {
        var (ocrClient, _, _) = Build(body: OcrBody);
        var ocr = await ocrClient.ReadDocumentAsync(
            Tenant, Image(1), KycDocumentType.Cni, Correlation, CancellationToken.None);

        var (faceClient, _, _) = Build(body: FaceMatchBody);
        var face = await faceClient.MatchFaceAsync(
            Tenant, Image(1), Image(2), Correlation, CancellationToken.None);

        return new ScoreRequest(
            Ocr: ocr.Value,
            FaceMatch: face.Value,
            Channel: KycChannel.Agency,
            RiskLevel: KycVigilanceLevel.Standard);
    }

    // ── Transport ────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Posts_to_the_configured_base_url_with_the_bearer_token_and_the_correlation_id()
    {
        var (client, handler, secrets) = Build();

        await client.ReadDocumentAsync(
            Tenant, Image(1, 2, 3), KycDocumentType.Cni, Correlation, CancellationToken.None);

        handler.Request!.Method.Should().Be(HttpMethod.Post);

        // The configured BaseUrl carries no trailing slash on purpose: without the normalisation
        // Uri would drop the last segment of a service mounted under a sub-path.
        handler.Request.RequestUri!.ToString().Should().Be("https://biometry.internal/v1/ocr");

        // Per call, not on the pooled client: DefaultRequestHeaders would hand one tenant's token
        // to the next tenant using the same HttpClient.
        handler.Request.Headers.Authorization!.Scheme.Should().Be("Bearer");
        handler.Request.Headers.Authorization.Parameter.Should().Be("biometry-token");

        handler.Request.Headers.GetValues("X-Correlation-Id")
            .Should().ContainSingle().Which.Should().Be(Correlation);

        // The token is a per-tenant vault entry, not configuration.
        await secrets.Received(1).GetValueAsync(
            Arg.Is<SecretKey>(k =>
                k.TenantId == Tenant
                && k.Scope == BiometrySecrets.Scope
                && k.EntityId == Guid.Empty
                && k.Name == BiometrySecrets.TokenName),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Sends_the_image_and_the_declared_document_type_the_service_requires()
    {
        var (client, handler, _) = Build();

        await client.ReadDocumentAsync(
            Tenant, Image(1, 2, 3), KycDocumentType.Passport, Correlation, CancellationToken.None);

        // doc_type is REQUIRED by /v1/ocr — it selects the extraction template — and was simply
        // never sent before, so every call would have been refused as a 422.
        handler.Payload.Should().Contain("\"doc_type\":\"PASSPORT\"");
        handler.Payload.Should().Contain("\"image_base64\":\"AQID\"");
    }

    [Fact]
    public async Task Refuses_to_call_the_service_when_no_token_is_stored()
    {
        var (client, handler, _) = Build(token: null);

        var result = await client.ReadDocumentAsync(
            Tenant, Image(1, 2, 3), KycDocumentType.Cni, Correlation, CancellationToken.None);

        result.IsUnavailable.Should().BeTrue();
        result.Code.Should().Be(BiometryCodes.NotConfigured);

        handler.Request.Should().BeNull("nothing should reach the network without a token");
    }

    [Fact]
    public async Task Answers_not_configured_when_no_base_url_is_set()
    {
        var (client, handler, _) = Build(configure: o => o.BaseUrl = "   ");

        var result = await client.ReadDocumentAsync(
            Tenant, Image(1), KycDocumentType.Cni, Correlation, CancellationToken.None);

        result.IsUnavailable.Should().BeTrue();
        result.Code.Should().Be(BiometryCodes.NotConfigured);
        handler.Request.Should().BeNull();
    }

    [Fact]
    public async Task Sends_the_selfie_and_the_document_image_under_the_names_the_service_expects()
    {
        var (client, handler, _) = Build(body: FaceMatchBody);

        await client.MatchFaceAsync(
            Tenant, Image(1, 2, 3), Image(4, 5, 6), Correlation, CancellationToken.None);

        handler.Request!.RequestUri!.AbsolutePath.Should().Be("/v1/face-match");
        handler.Payload.Should().Contain("\"selfie_base64\":\"BAUG\"");

        // The full document image, not a cropped portrait: the service runs its own detector, and
        // doc_face_base64 is the field for a face we had already cut out — we have none.
        handler.Payload.Should().Contain("\"doc_image_base64\":\"AQID\"");
    }

    [Fact]
    public async Task Hands_the_scorer_back_the_services_own_answers_plus_the_channel_and_risk()
    {
        var (client, handler, _) = Build(body: ScoreBody);

        await client.ScoreAsync(Tenant, await ScorableAsync(), Correlation, CancellationToken.None);

        handler.Request!.RequestUri!.AbsolutePath.Should().Be("/v1/score");

        // Verbatim: the scorer is stateless and grades the readings it produced, not a projection
        // of them — which is why ocr_result carries the anomalies and the quality this module drops.
        handler.Payload.Should().Contain("\"ocr_result\"").And.Contain("\"face_result\"");
        handler.Payload.Should().Contain("\"EXPIRED_DOCUMENT\"");
        handler.Payload.Should().Contain("\"threshold\":0.62");

        // Required, and part of the weighting: a remote capture is not graded like a counter one.
        handler.Payload.Should().Contain("\"channel\":\"BRANCH\"");
        handler.Payload.Should().Contain("\"risk_level\":\"STANDARD\"");
    }

    [Fact]
    public async Task Refuses_to_score_without_the_services_own_payloads()
    {
        // Rebuilding them from this module's projections would have the scorer grade a document
        // whose anomalies, quality and MRZ checks had been dropped — a different document.
        var (client, handler, _) = Build(body: ScoreBody);

        var result = await client.ScoreAsync(
            Tenant,
            new ScoreRequest(null, null, KycChannel.Agency, KycVigilanceLevel.Standard),
            Correlation,
            CancellationToken.None);

        result.IsUnavailable.Should().BeTrue();
        result.Code.Should().Be(BiometryCodes.UnexpectedResponse);
        handler.Request.Should().BeNull();
    }

    // ── Success mapping ──────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Maps_an_ocr_reading_from_the_services_own_field_objects()
    {
        var (client, _, _) = Build(body: OcrBody);

        var result = await client.ReadDocumentAsync(
            Tenant, Image(1), KycDocumentType.Cni, Correlation, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.DocumentType.Should().Be("CNI");

        // One object per field on the wire, two flat maps in the module.
        result.Value.Fields["surname"].Should().Be("OUATTARA");
        result.Value.FieldConfidences["given_names"].Should().Be(0.91);

        result.Value.Fields.Should().NotContainKey("place_of_birth",
            "a null value means the field is not on the document, not that it read as empty");

        // model_versions.service, which the previous mappers looked for as service_version and
        // therefore never found — every call degraded to BIOMETRY_UNEXPECTED_RESPONSE.
        result.Value.ServiceVersion.Should().Be(ServiceVersion);

        // A failed checksum is a reading, not a rejection: whether it blocks the file is M02's
        // call, taken on the whole verification.
        result.Value.Mrz!.ChecksumValid.Should().BeFalse();
        result.Value.Mrz.Fields["document_number"].Should().Be("CI0012345678");
        result.Value.Mrz.Fields["birth_date"].Should().Be("1987-04-02");

        // Never the raw MRZ line: that line IS the document number spelled out, and the evidence
        // table encrypts the number in the column next to it.
        result.Value.Mrz.Raw.Should().BeNull();
    }

    [Fact]
    public async Task Maps_a_face_match_with_the_threshold_the_verdict_was_taken_against()
    {
        var (client, _, _) = Build(body: FaceMatchBody);

        var result = await client.MatchFaceAsync(
            Tenant, Image(1), Image(2), Correlation, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Similarity.Should().Be(0.91);
        result.Value.IsMatch.Should().BeTrue();
        result.Value.Threshold.Should().Be(0.62);
        result.Value.PortraitQuality.Should().Be(0.80, "quality.document.score");
        result.Value.SelfieQuality.Should().Be(0.77, "quality.selfie.score");
        result.Value.ServiceVersion.Should().Be(ServiceVersion);
    }

    [Fact]
    public async Task Maps_a_confidence_score_with_its_breakdown_and_flag_codes()
    {
        var (client, _, _) = Build(body: ScoreBody);

        var result = await client.ScoreAsync(
            Tenant, await ScorableAsync(), Correlation, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Score.Should().Be(73, "global_score");

        // One object per criterion on the wire; the module stores whole points, rounded away from
        // zero so a criterion worth half a point does not display as contributing nothing.
        result.Value.Breakdown["face"].Should().Be(28);
        result.Value.Breakdown["document"].Should().Be(30);

        result.Value.Flags.Should().ContainSingle().Which.Should().Be("NAME_MISMATCH");
        result.Value.ServiceVersion.Should().Be(ServiceVersion);
    }

    [Theory]
    [InlineData("VALIDATED", BiometryConfidenceLevel.High)]
    [InlineData("REVIEW_REQUIRED", BiometryConfidenceLevel.Medium)]
    // REJECTED is Low and NOT a refusal: refusing a file stays M02's decision, taken from the face
    // verdict and the tenant's rejection floor. A model does not close a customer's file.
    [InlineData("REJECTED", BiometryConfidenceLevel.Low)]
    internal async Task Maps_the_services_own_level_vocabulary(string level, BiometryConfidenceLevel expected)
    {
        var body = ScoreBody.Replace("\"REVIEW_REQUIRED\"", $"\"{level}\"", StringComparison.Ordinal);
        var (client, _, _) = Build(body: body);

        var result = await client.ScoreAsync(
            Tenant, await ScorableAsync(), Correlation, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Level.Should().Be(expected,
            "parsed as Low/Medium/High, this never matched and the level was silently replaced "
            + "by our own thresholds on every single call");
    }

    [Fact]
    public async Task Refuses_a_reading_it_cannot_attribute_to_a_service_version()
    {
        // A verification is kept as evidence; a reading nobody can trace back to a model is worth
        // less than no reading at all.
        var body = OcrBody.Replace("\"model_versions\": { \"service\": \"biometry-2.4.1\" }",
                                   "\"model_versions\": { \"service\": \"\" }", StringComparison.Ordinal);

        var (client, _, _) = Build(body: body);

        var result = await client.ReadDocumentAsync(
            Tenant, Image(1), KycDocumentType.Cni, Correlation, CancellationToken.None);

        result.IsUnavailable.Should().BeTrue();
        result.Code.Should().Be(BiometryCodes.UnexpectedResponse);
    }

    // ── Functional vs technical ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task Treats_a_422_carrying_a_service_code_as_a_functional_rejection()
    {
        // This one the business records and acts on: the agent is asked for a better photo. The
        // code is read from the service's OWN envelope — the previous version looked for a string
        // at the root of the body, so this rejection degraded to a technical result and the agent
        // was never told what to re-shoot.
        var (client, _, _) = Build(
            status: HttpStatusCode.UnprocessableContent,
            body: """{"error":{"code":"IMAGE_QUALITY_TOO_LOW","message":"flou"}}""");

        var result = await client.ReadDocumentAsync(
            Tenant, Image(1), KycDocumentType.Cni, Correlation, CancellationToken.None);

        result.IsRejected.Should().BeTrue();
        result.IsUnavailable.Should().BeFalse();
        result.Code.Should().Be(BiometryCodes.ImageQualityTooLow);
        result.Detail.Should().Contain("flou");
    }

    [Fact]
    public async Task Treats_a_413_as_a_rejection_of_the_capture()
    {
        // The service refuses the image as too large: that is a statement about the capture, and
        // the agent can act on it by re-shooting at a lower resolution.
        var (client, _, _) = Build(
            status: HttpStatusCode.RequestEntityTooLarge,
            body: """{"error":{"code":"IMAGE_TOO_LARGE","message":"8 MB"}}""");

        var result = await client.ReadDocumentAsync(
            Tenant, Image(1), KycDocumentType.Cni, Correlation, CancellationToken.None);

        result.IsRejected.Should().BeTrue();
        result.Code.Should().Be("IMAGE_TOO_LARGE");
    }

    [Fact]
    public async Task Treats_a_503_models_not_ready_as_technical()
    {
        // The service is alive but its models are still loading — nothing was examined, so the
        // file stays in Verifying and Hangfire replays it.
        var (client, _, _) = Build(
            status: HttpStatusCode.ServiceUnavailable,
            body: """{"error":{"code":"MODELS_NOT_READY","message":"loading"}}""");

        var result = await client.MatchFaceAsync(
            Tenant, Image(1), Image(2), Correlation, CancellationToken.None);

        result.IsUnavailable.Should().BeTrue();
        result.IsRejected.Should().BeFalse();
        result.Code.Should().Be(BiometryCodes.ModelsNotReady);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.InternalServerError)]
    public async Task Treats_every_other_failing_status_as_technical(HttpStatusCode status)
    {
        // A bad token, a wrong URL or a crash says nothing about the photo. Recording any of them
        // as a rejection would reject an honest client because of our own problem.
        var (client, _, _) = Build(status: status, body: "nope");

        var result = await client.ReadDocumentAsync(
            Tenant, Image(1), KycDocumentType.Cni, Correlation, CancellationToken.None);

        result.IsUnavailable.Should().BeTrue();
        result.Code.Should().Be(BiometryCodes.ServiceUnavailable);
    }

    [Fact]
    public async Task Treats_a_422_without_a_service_code_as_technical()
    {
        // There is no verdict to record, so it must not become an invented rejection reason.
        var (client, _, _) = Build(
            status: HttpStatusCode.UnprocessableContent,
            body: """{"message":"something went wrong"}""");

        var result = await client.ReadDocumentAsync(
            Tenant, Image(1), KycDocumentType.Cni, Correlation, CancellationToken.None);

        result.IsUnavailable.Should().BeTrue();
        result.Code.Should().Be(BiometryCodes.UnexpectedResponse);
    }

    [Fact]
    public async Task Treats_an_unparseable_body_as_technical_rather_than_crashing()
    {
        var (client, _, _) = Build(body: "<html><body>502 Bad Gateway</body></html>");

        var result = await client.ReadDocumentAsync(
            Tenant, Image(1), KycDocumentType.Cni, Correlation, CancellationToken.None);

        result.IsUnavailable.Should().BeTrue();
        result.Code.Should().Be(BiometryCodes.UnexpectedResponse);
    }

    [Fact]
    public async Task Treats_an_unreachable_service_as_technical()
    {
        var handler = new CapturingHandler(
            _ => throw new HttpRequestException("Connection refused"));

        var (client, _, _) = Build(handler);

        var result = await client.ReadDocumentAsync(
            Tenant, Image(1), KycDocumentType.Cni, Correlation, CancellationToken.None);

        result.IsUnavailable.Should().BeTrue();
        result.Code.Should().Be(BiometryCodes.ServiceUnavailable);
    }

    [Fact]
    public async Task Refuses_an_answer_over_the_size_cap()
    {
        // A misrouted URL answering with a login page or an image must not be buffered into the
        // request's memory and handed to a JSON parser. The distinct code is what tells an operator
        // to look at the URL rather than at the service.
        var (client, _, _) = Build(
            body: new string('x', 4096),
            configure: o => o.MaxResponseBytes = 1024);

        var result = await client.ReadDocumentAsync(
            Tenant, Image(1), KycDocumentType.Cni, Correlation, CancellationToken.None);

        result.IsUnavailable.Should().BeTrue();
        result.Code.Should().Be(BiometryCodes.ResponseTooLarge);
    }

    // ── Timeouts and cancellation ────────────────────────────────────────────────────────────

    [Fact]
    public async Task Treats_its_own_per_endpoint_timeout_as_technical()
    {
        var handler = new CapturingHandler(async ct =>
        {
            await Task.Delay(Timeout.Infinite, ct);
            return new HttpResponseMessage(HttpStatusCode.OK);
        });

        var (client, _, _) = Build(handler, configure: o => o.OcrTimeoutSeconds = 1);

        var result = await client.ReadDocumentAsync(
            Tenant, Image(1), KycDocumentType.Cni, Correlation, CancellationToken.None);

        result.IsUnavailable.Should().BeTrue();
        result.Code.Should().Be(BiometryCodes.Timeout);
    }

    [Fact]
    public async Task Lets_the_callers_own_cancellation_through_instead_of_reporting_an_outage()
    {
        // A Hangfire job being stopped is not a service failure, and must not be recorded as one.
        var handler = new CapturingHandler(async ct =>
        {
            await Task.Delay(Timeout.Infinite, ct);
            return new HttpResponseMessage(HttpStatusCode.OK);
        });

        var (client, _, _) = Build(handler);

        using var caller = new CancellationTokenSource();
        caller.CancelAfter(TimeSpan.FromMilliseconds(200));

        var act = async () => await client.ReadDocumentAsync(
            Tenant, Image(1), KycDocumentType.Cni, Correlation, caller.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }
}
