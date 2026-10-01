namespace Sankore.Modules.Kyc.Tests.Infrastructure.Biometry;

using FluentAssertions;
using Sankore.Modules.Kyc.Infrastructure.Biometry;
using Xunit;

public sealed class FakeBiometryClientTests
{
    private static readonly Guid Tenant = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private const string Correlation = "kyc-verification-7";

    private static BiometryImage Image(params byte[] content) => new(content, "image/jpeg");

    [Fact]
    public async Task Answers_a_plausible_success_on_all_three_endpoints_out_of_the_box()
    {
        var fake = new FakeBiometryClient();

        var ocr = await fake.ReadDocumentAsync(Tenant, Image(1), Correlation, CancellationToken.None);
        var face = await fake.MatchFaceAsync(Tenant, Image(1), Image(2), Correlation, CancellationToken.None);
        var score = await fake.ScoreAsync(Tenant, new ScoreRequest(ocr.Value, face.Value), Correlation, CancellationToken.None);

        ocr.IsSuccess.Should().BeTrue();
        ocr.Value.DocumentType.Should().Be("CNI");
        ocr.Value.Fields["surname"].Should().Be("OUATTARA");
        ocr.Value.Mrz!.ChecksumValid.Should().BeTrue();

        face.IsSuccess.Should().BeTrue();
        face.Value.IsMatch.Should().BeTrue();

        score.IsSuccess.Should().BeTrue();
        score.Value.Score.Should().Be(82);
        score.Value.Level.Should().Be(BiometryConfidenceLevel.High);

        // Every answer is attributable, like the real service's.
        ocr.Value.ServiceVersion.Should().Be(FakeBiometryClient.ServiceVersion);
        face.Value.ServiceVersion.Should().Be(FakeBiometryClient.ServiceVersion);
        score.Value.ServiceVersion.Should().Be(FakeBiometryClient.ServiceVersion);
    }

    [Fact]
    public async Task Answers_the_same_thing_twice_for_the_same_input()
    {
        // No clock, no randomness, no hashing of the bytes: a suite that asserts on a field must
        // keep passing tomorrow and on someone else's machine.
        var fake = new FakeBiometryClient();

        var first = await fake.ReadDocumentAsync(Tenant, Image(1, 2, 3), Correlation, CancellationToken.None);
        var second = await fake.ReadDocumentAsync(Tenant, Image(9, 9, 9), "another-id", CancellationToken.None);

        second.Value.Should().BeEquivalentTo(first.Value);
    }

    [Fact]
    public async Task Forces_a_functional_rejection_on_one_endpoint()
    {
        var fake = new FakeBiometryClient
        {
            ForcedFaceMatch = BiometryResult<FaceMatch>.Rejected(BiometryCodes.NoFaceDetected),
        };

        var face = await fake.MatchFaceAsync(Tenant, Image(1), Image(2), Correlation, CancellationToken.None);
        var ocr = await fake.ReadDocumentAsync(Tenant, Image(1), Correlation, CancellationToken.None);

        face.IsRejected.Should().BeTrue();
        face.Code.Should().Be(BiometryCodes.NoFaceDetected);

        ocr.IsSuccess.Should().BeTrue("forcing one endpoint must not disturb the others");
    }

    [Fact]
    public async Task Forces_a_functional_rejection_everywhere()
    {
        var fake = FakeBiometryClient.Rejecting(BiometryCodes.DocumentUnreadable);

        var ocr = await fake.ReadDocumentAsync(Tenant, Image(1), Correlation, CancellationToken.None);
        var score = await fake.ScoreAsync(Tenant, new ScoreRequest(null, null), Correlation, CancellationToken.None);

        ocr.IsRejected.Should().BeTrue();
        ocr.Code.Should().Be(BiometryCodes.DocumentUnreadable);
        score.IsRejected.Should().BeTrue();
    }

    [Fact]
    public async Task Forces_a_technical_unavailability_everywhere()
    {
        var fake = FakeBiometryClient.Unavailable(BiometryCodes.ModelsNotReady);

        var ocr = await fake.ReadDocumentAsync(Tenant, Image(1), Correlation, CancellationToken.None);

        ocr.IsUnavailable.Should().BeTrue();
        ocr.IsRejected.Should().BeFalse();
        ocr.Code.Should().Be(BiometryCodes.ModelsNotReady);
    }

    [Fact]
    public void Refuses_to_hand_out_a_payload_that_does_not_exist()
    {
        var failed = BiometryResult<OcrReading>.Unavailable(BiometryCodes.Timeout);

        var act = () => failed.Value;

        act.Should().Throw<InvalidOperationException>().WithMessage("*Unavailable*");
    }

    [Fact]
    public async Task Derives_the_match_verdict_from_the_similarity_unless_it_is_pinned()
    {
        var derived = new FakeBiometryClient { Similarity = 0.41 };
        var pinned = new FakeBiometryClient { Similarity = 0.41, IsMatch = true };

        var derivedMatch = await derived.MatchFaceAsync(Tenant, Image(1), Image(2), Correlation, CancellationToken.None);
        var pinnedMatch = await pinned.MatchFaceAsync(Tenant, Image(1), Image(2), Correlation, CancellationToken.None);

        derivedMatch.Value.IsMatch.Should().BeFalse("a 0.41 similarity reported as a match would be nonsense");
        pinnedMatch.Value.IsMatch.Should().BeTrue();
    }

    [Fact]
    public async Task Keeps_the_score_breakdown_consistent_with_the_score_it_is_given()
    {
        var fake = new FakeBiometryClient { Score = 37 };
        fake.Flags.Add("EXPIRED_DOCUMENT");

        var score = await fake.ScoreAsync(Tenant, new ScoreRequest(null, null), Correlation, CancellationToken.None);

        score.Value.Score.Should().Be(37);
        score.Value.Level.Should().Be(BiometryConfidenceLevel.Low);
        score.Value.Breakdown.Values.Sum().Should().Be(37);
        score.Value.Flags.Should().ContainSingle().Which.Should().Be("EXPIRED_DOCUMENT");
    }

    [Fact]
    public async Task Lets_a_test_overwrite_a_single_ocr_field_without_restating_the_document()
    {
        var fake = new FakeBiometryClient();
        fake.Fields["expiry_date"] = "2019-01-31";
        fake.MrzRaw = null;

        var ocr = await fake.ReadDocumentAsync(Tenant, Image(1), Correlation, CancellationToken.None);

        ocr.Value.Fields["expiry_date"].Should().Be("2019-01-31");
        ocr.Value.Fields["surname"].Should().Be("OUATTARA");
        ocr.Value.Mrz.Should().BeNull("a passport-less document has no machine-readable zone");
    }

    [Fact]
    public async Task Records_who_called_it_and_under_which_correlation_id()
    {
        var fake = new FakeBiometryClient();
        var request = new ScoreRequest(null, null, DocumentType: "PASSPORT");

        await fake.ReadDocumentAsync(Tenant, Image(1), Correlation, CancellationToken.None);
        await fake.MatchFaceAsync(Tenant, Image(1), Image(2), Correlation, CancellationToken.None);
        await fake.ScoreAsync(Tenant, request, Correlation, CancellationToken.None);

        fake.Calls.Select(c => c.Endpoint).Should().Equal("ocr", "face-match", "score");
        fake.Calls.Should().OnlyContain(c => c.TenantId == Tenant && c.CorrelationId == Correlation);

        fake.LastSelfie!.Content.Should().Equal((byte)2);
        fake.LastScoreRequest.Should().BeSameAs(request);
    }

    [Fact]
    public async Task Honours_cancellation_like_the_real_client()
    {
        var fake = new FakeBiometryClient();

        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        var act = async () => await fake.ReadDocumentAsync(Tenant, Image(1), Correlation, cancelled.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }
}
