namespace Sankore.Modules.Kyc.Tests.Infrastructure.Biometry;

using System.Net;
using System.Text;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Sankore.Modules.Kyc.Infrastructure.Biometry;
using Sankore.Shared.Kernel;
using Xunit;

public sealed class HttpBiometryClientTests
{
    private static readonly Guid Tenant = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private const string Correlation = "kyc-verification-42";

    private const string OcrBody = """
        {
          "document_type": "CNI",
          "fields": { "surname": "OUATTARA", "given_names": "AWA" },
          "field_confidences": { "surname": 0.98, "given_names": 0.91 },
          "mrz": {
            "raw": "I<CIVCI0012345678<<<<<<<<<<<<<<",
            "checksum_valid": false,
            "fields": { "document_number": "CI0012345678" }
          },
          "service_version": "biometry-2.4.1"
        }
        """;

    private const string FaceMatchBody = """
        {
          "similarity": 0.91,
          "is_match": true,
          "portrait_quality": 0.80,
          "selfie_quality": 0.77,
          "model_version": "arcface-r100-v3",
          "service_version": "biometry-2.4.1"
        }
        """;

    private const string ScoreBody = """
        {
          "score": 73,
          "level": "Medium",
          "breakdown": { "document": 30, "face": 28, "consistency": 15 },
          "flags": ["NAME_MISMATCH"],
          "service_version": "biometry-2.4.1"
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

    /// <summary>
    /// Non-seekable, so <see cref="StreamContent"/> declares no Content-Length and the client has
    /// to discover the size while reading. Counts what it actually handed out.
    /// </summary>
    private sealed class CountingStream(int totalBytes) : Stream
    {
        private int _remaining = totalBytes;

        public int BytesRead { get; private set; }

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            var served = Math.Min(count, _remaining);
            _remaining -= served;
            BytesRead += served;
            Array.Fill(buffer, (byte)'x', offset, served);

            return served;
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private static BiometryImage Image(params byte[] content) => new(content, "image/jpeg", "cni.jpg");

    private static (HttpBiometryClient Client, CapturingHandler Handler, ISecretsModule Secrets) Build(
        CapturingHandler handler,
        string? token = "biometry-token",
        Action<BiometryOptions>? configure = null)
    {
        var options = new BiometryOptions { BaseUrl = "https://biometry.internal" };
        configure?.Invoke(options);

        var secrets = Substitute.For<ISecretsModule>();
        secrets.GetValueAsync(Arg.Any<SecretKey>(), Arg.Any<CancellationToken>()).Returns(token);

        var factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient(HttpBiometryClient.HttpClientName).Returns(_ => new HttpClient(handler));

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

    // ── Transport ────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Posts_to_the_configured_base_url_with_the_bearer_token_and_the_correlation_id()
    {
        var (client, handler, secrets) = Build();

        await client.ReadDocumentAsync(Tenant, Image(1, 2, 3), Correlation, CancellationToken.None);

        handler.Request!.Method.Should().Be(HttpMethod.Post);

        // The configured BaseUrl carries no trailing slash on purpose: without the normalisation
        // Uri would drop the last segment of a service mounted under a sub-path.
        handler.Request.RequestUri!.ToString().Should().Be("https://biometry.internal/v1/ocr");

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
    public async Task Refuses_to_call_the_service_when_no_token_is_stored()
    {
        var (client, handler, _) = Build(token: null);

        var result = await client.ReadDocumentAsync(
            Tenant, Image(1, 2, 3), Correlation, CancellationToken.None);

        result.IsUnavailable.Should().BeTrue();
        result.Code.Should().Be(BiometryCodes.NotConfigured);

        handler.Request.Should().BeNull("nothing should reach the network without a token");
    }

    [Fact]
    public async Task Answers_not_configured_when_no_base_url_is_set()
    {
        var (client, handler, _) = Build(configure: o => o.BaseUrl = "   ");

        var result = await client.ScoreAsync(
            Tenant, new ScoreRequest(null, null), Correlation, CancellationToken.None);

        result.IsUnavailable.Should().BeTrue();
        result.Code.Should().Be(BiometryCodes.NotConfigured);
        handler.Request.Should().BeNull();
    }

    [Fact]
    public async Task Sends_both_images_base64_encoded_to_the_face_match_endpoint()
    {
        var (client, handler, _) = Build(body: FaceMatchBody);

        await client.MatchFaceAsync(
            Tenant, Image(1, 2, 3), Image(4, 5, 6), Correlation, CancellationToken.None);

        handler.Request!.RequestUri!.AbsolutePath.Should().Be("/v1/face-match");
        handler.Payload.Should().Contain("\"document_portrait\"").And.Contain("\"AQID\"");
        handler.Payload.Should().Contain("\"selfie\"").And.Contain("\"BAUG\"");
    }

    [Fact]
    public async Task Sends_the_readings_the_stateless_scorer_has_to_weigh()
    {
        var (client, handler, _) = Build(body: ScoreBody);

        var request = new ScoreRequest(
            Ocr: new OcrReading(
                "CNI",
                new Dictionary<string, string> { ["surname"] = "OUATTARA" },
                new Dictionary<string, double> { ["surname"] = 0.98 },
                null,
                "biometry-2.4.1"),
            FaceMatch: new FaceMatch(0.91, true, 0.8, 0.77, "arcface-r100-v3", "biometry-2.4.1"),
            DeclaredFields: new Dictionary<string, string> { ["surname"] = "OUATTARA" },
            DocumentType: "CNI");

        await client.ScoreAsync(Tenant, request, Correlation, CancellationToken.None);

        handler.Request!.RequestUri!.AbsolutePath.Should().Be("/v1/score");
        handler.Payload.Should().Contain("\"face_match\"").And.Contain("\"declared_fields\"");

        // Dictionary keys are the service's own field names and must travel verbatim — the
        // snake_case policy applies to properties only.
        handler.Payload.Should().Contain("\"surname\"");
    }

    // ── Success mapping ──────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Maps_an_ocr_reading_with_its_service_version()
    {
        var (client, _, _) = Build(body: OcrBody);

        var result = await client.ReadDocumentAsync(
            Tenant, Image(1), Correlation, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.DocumentType.Should().Be("CNI");
        result.Value.Fields["surname"].Should().Be("OUATTARA");
        result.Value.FieldConfidences["given_names"].Should().Be(0.91);
        result.Value.ServiceVersion.Should().Be("biometry-2.4.1");

        // A failed MRZ checksum is a reading, not a rejection: whether it blocks the file is M02's
        // call, taken on the whole verification.
        result.Value.Mrz!.ChecksumValid.Should().BeFalse();
        result.Value.Mrz.Fields["document_number"].Should().Be("CI0012345678");
    }

    [Fact]
    public async Task Maps_a_face_match_with_the_model_that_produced_it()
    {
        var (client, _, _) = Build(body: FaceMatchBody);

        var result = await client.MatchFaceAsync(
            Tenant, Image(1), Image(2), Correlation, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Similarity.Should().Be(0.91);
        result.Value.IsMatch.Should().BeTrue();
        result.Value.PortraitQuality.Should().Be(0.80);
        result.Value.ModelVersion.Should().Be("arcface-r100-v3");
        result.Value.ServiceVersion.Should().Be("biometry-2.4.1");
    }

    [Fact]
    public async Task Maps_a_confidence_score_with_its_breakdown_and_flags()
    {
        var (client, _, _) = Build(body: ScoreBody);

        var result = await client.ScoreAsync(
            Tenant, new ScoreRequest(null, null), Correlation, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Score.Should().Be(73);
        result.Value.Level.Should().Be(BiometryConfidenceLevel.Medium);
        result.Value.Breakdown["face"].Should().Be(28);
        result.Value.Flags.Should().ContainSingle().Which.Should().Be("NAME_MISMATCH");
    }

    [Fact]
    public async Task Falls_back_to_our_own_thresholds_when_the_service_names_a_level_we_do_not_know()
    {
        var (client, _, _) = Build(body: """
            {"score":91,"level":"VERY_HIGH","service_version":"biometry-3.0.0"}
            """);

        var result = await client.ScoreAsync(
            Tenant, new ScoreRequest(null, null), Correlation, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Level.Should().Be(BiometryConfidenceLevel.High);
    }

    // ── Functional vs technical ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task Treats_a_422_carrying_a_service_code_as_a_functional_rejection()
    {
        // This one the business records and acts on: the agent is asked for a better photo.
        var (client, _, _) = Build(
            status: HttpStatusCode.UnprocessableContent,
            body: """{"error_code":"IMAGE_QUALITY_TOO_LOW","message":"flou"}""");

        var result = await client.ReadDocumentAsync(
            Tenant, Image(1), Correlation, CancellationToken.None);

        result.IsRejected.Should().BeTrue();
        result.IsUnavailable.Should().BeFalse();
        result.Code.Should().Be(BiometryCodes.ImageQualityTooLow);
        result.Detail.Should().Contain("flou");
    }

    [Fact]
    public async Task Treats_a_503_models_not_ready_as_technical()
    {
        // The service is alive but its models are still loading — nothing was examined, so the
        // file stays in Verifying and Hangfire replays it.
        var (client, _, _) = Build(
            status: HttpStatusCode.ServiceUnavailable,
            body: "MODELS_NOT_READY");

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
            Tenant, Image(1), Correlation, CancellationToken.None);

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
            Tenant, Image(1), Correlation, CancellationToken.None);

        result.IsUnavailable.Should().BeTrue();
        result.Code.Should().Be(BiometryCodes.UnexpectedResponse);
    }

    [Fact]
    public async Task Treats_an_unparseable_body_as_technical_rather_than_crashing()
    {
        var (client, _, _) = Build(body: "<html><body>502 Bad Gateway</body></html>");

        var result = await client.ReadDocumentAsync(
            Tenant, Image(1), Correlation, CancellationToken.None);

        result.IsUnavailable.Should().BeTrue();
        result.Code.Should().Be(BiometryCodes.UnexpectedResponse);
        result.Detail.Should().Contain("502 Bad Gateway");
    }

    [Fact]
    public async Task Refuses_a_reading_it_cannot_attribute_to_a_service_version()
    {
        // A verification is kept as evidence; a reading nobody can trace back to a model is worth
        // less than no reading at all.
        var (client, _, _) = Build(body: """{"document_type":"CNI","fields":{"surname":"AWA"}}""");

        var result = await client.ReadDocumentAsync(
            Tenant, Image(1), Correlation, CancellationToken.None);

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
            Tenant, Image(1), Correlation, CancellationToken.None);

        result.IsUnavailable.Should().BeTrue();
        result.Code.Should().Be(BiometryCodes.ServiceUnavailable);
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
            Tenant, Image(1), Correlation, CancellationToken.None);

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

        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        var act = async () => await client.ReadDocumentAsync(
            Tenant, Image(1), Correlation, cancelled.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    // ── Size cap ─────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Refuses_a_response_whose_declared_length_is_over_the_cap()
    {
        var (client, _, _) = Build(
            body: new string('x', 5_000),
            configure: o => o.MaxResponseBytes = 1_024);

        var result = await client.ReadDocumentAsync(
            Tenant, Image(1), Correlation, CancellationToken.None);

        result.IsUnavailable.Should().BeTrue();
        result.Code.Should().Be(BiometryCodes.ResponseTooLarge);
    }

    [Fact]
    public async Task Refuses_an_undeclared_oversized_response_without_reading_all_of_it()
    {
        var stream = new CountingStream(64 * 1024);

        var handler = new CapturingHandler(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StreamContent(stream),
        }));

        var (client, _, _) = Build(handler, configure: o => o.MaxResponseBytes = 1_024);

        var result = await client.ReadDocumentAsync(
            Tenant, Image(1), Correlation, CancellationToken.None);

        result.IsUnavailable.Should().BeTrue();
        result.Code.Should().Be(BiometryCodes.ResponseTooLarge);

        // Greater than zero proves the streaming guard ran rather than the declared-length
        // shortcut; one byte past the cap is all it takes to know we are over it, and buffering
        // the rest is exactly what the cap exists to prevent.
        stream.BytesRead.Should().BePositive().And.BeLessThanOrEqualTo(1_025);
    }
}
