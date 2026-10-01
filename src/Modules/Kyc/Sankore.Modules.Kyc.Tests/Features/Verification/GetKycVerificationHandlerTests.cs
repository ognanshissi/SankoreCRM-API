namespace Sankore.Modules.Kyc.Tests.Features.Verification;

using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Sankore.Modules.Kyc.Domain;
using Sankore.Modules.Kyc.Features.Verification.GetVerification;
using Sankore.Modules.Kyc.Infrastructure;
using Sankore.Modules.Kyc.Tests.TestSupport;
using Xunit;

/// <summary>
/// The read side of a verification. Two properties carry the weight: the panel shows the LATEST
/// assessment rather than the history of superseded ones, and it carries nothing a caller would need
/// <c>kyc:document:reveal</c> for — which is what lets it sit behind <c>kyc:read</c> at all.
/// </summary>
public sealed class GetKycVerificationHandlerTests : IDisposable
{
    private readonly Guid _tenantId = Guid.NewGuid();
    private readonly TestKycDbContextFactory _factory;
    private readonly KycDbContext _db;
    private readonly FakeTimeProvider _clock = new();

    public GetKycVerificationHandlerTests()
    {
        _factory = new TestKycDbContextFactory(_tenantId);
        _db = _factory.CreateContext();
    }

    public void Dispose()
    {
        _db.Dispose();
        _factory.Dispose();
    }

    private GetKycVerificationHandler Handler() =>
        new(_db, NullLogger<GetKycVerificationHandler>.Instance);

    private KycFile SeedFile()
    {
        var file = KycFile.Open(_tenantId, Guid.NewGuid(), KycChannel.Agency, Guid.NewGuid(), _clock);
        _db.KycFiles.Add(file);
        return file;
    }

    private void SeedAssessment(
        Guid fileId, int score, KycConfidenceLevel level, string trigger,
        Dictionary<string, int>? breakdown = null, List<string>? flags = null,
        string? breakdownJson = null)
    {
        _db.KycConfidenceAssessments.Add(KycConfidenceAssessment.Create(
            _tenantId, fileId, score, level, trigger, _clock,
            breakdownJson: breakdownJson ?? JsonSerializer.Serialize(breakdown ?? []),
            flagsJson: JsonSerializer.Serialize(flags ?? []),
            serviceVersion: "flask-1.2.3"));
    }

    private void SeedFace(Guid fileId, int attempt, double similarity, bool isMatch)
    {
        _db.KycFaceVerifications.Add(KycFaceVerification.Create(
            tenantId: _tenantId, kycFileId: fileId, attempt: attempt,
            similarityScore: similarity, isMatch: isMatch, clock: _clock,
            qualityScoresJson: JsonSerializer.Serialize(new { portrait = 0.88, selfie = 0.79 }),
            modelVersion: "arcface-3"));
    }

    private async Task<KycVerificationDto> ReadAsync(Guid fileId)
    {
        await _db.SaveChangesAsync();
        var result = await Handler().Handle(new GetKycVerificationQuery(fileId), default);
        result.IsSuccess.Should().BeTrue();
        return result.Value!;
    }

    // ------------------------------------------------------------------ the latest, not the history

    [Fact]
    public async Task The_panel_shows_the_latest_assessment_not_the_one_a_correction_superseded()
    {
        var file = SeedFile();
        SeedAssessment(file.Id, 54, KycConfidenceLevel.Medium, "VERIFICATION");
        _clock.Advance(TimeSpan.FromMinutes(5));
        SeedAssessment(file.Id, 81, KycConfidenceLevel.High, "FIELD_CORRECTION");

        var dto = await ReadAsync(file.Id);

        dto.ConfidenceScore.Should().Be(81);
        dto.ConfidenceLevel.Should().Be(nameof(KycConfidenceLevel.High));
        dto.Trigger.Should().Be("FIELD_CORRECTION",
            "the panel has to say what moved the score, or an agent argues with a number nobody explains");
    }

    [Fact]
    public async Task The_latest_face_comparison_is_the_highest_attempt_not_the_newest_timestamp()
    {
        var file = SeedFile();

        // Both recorded in the same tick: ordering on CreatedAt would be arbitrary, and the attempt
        // number is the sequence that actually exists on the file.
        SeedFace(file.Id, attempt: 1, similarity: 0.42, isMatch: false);
        SeedFace(file.Id, attempt: 2, similarity: 0.93, isMatch: true);

        var dto = await ReadAsync(file.Id);

        dto.Face!.Attempt.Should().Be(2);
        dto.Face.IsMatch.Should().BeTrue();
    }

    // ------------------------------------------------------------------ the face numbers

    [Theory]
    [InlineData(0.93, 93)]
    [InlineData(0.0, 0)]
    [InlineData(1.0, 100)]
    [InlineData(0.495, 50)]  // away from zero, not banker's rounding to 49
    public async Task Similarity_is_reported_as_a_whole_percent(double similarity, int expected)
    {
        var file = SeedFile();
        SeedFace(file.Id, 1, similarity, isMatch: true);

        (await ReadAsync(file.Id)).Face!.MatchPercent.Should().Be(expected);
    }

    [Fact]
    public async Task The_match_verdict_is_the_services_own_and_is_never_re_derived_from_the_percent()
    {
        var file = SeedFile();

        // A high similarity the service nonetheless refused: its threshold is not ours to guess.
        SeedFace(file.Id, 1, similarity: 0.91, isMatch: false);

        var face = (await ReadAsync(file.Id)).Face!;

        face.MatchPercent.Should().Be(91);
        face.IsMatch.Should().BeFalse();
    }

    [Fact]
    public async Task Capture_quality_comes_back_split_by_image()
    {
        var file = SeedFile();
        SeedFace(file.Id, 1, 0.9, true);

        var face = (await ReadAsync(file.Id)).Face!;

        face.PortraitQuality.Should().BeApproximately(0.88, 0.001);
        face.SelfieQuality.Should().BeApproximately(0.79, 0.001);
    }

    // ------------------------------------------------------------------ the breakdown as received

    [Fact]
    public async Task The_breakdown_keeps_the_services_own_criterion_names()
    {
        var file = SeedFile();
        SeedAssessment(file.Id, 70, KycConfidenceLevel.Medium, "VERIFICATION",
            breakdown: new Dictionary<string, int> { ["mrz_checksum"] = 20, ["face"] = 30, ["something_new"] = 20 },
            flags: ["EXPIRED_DOCUMENT"]);

        var dto = await ReadAsync(file.Id);

        dto.Breakdown.Should().HaveCount(3).And.ContainKey("something_new",
            "the biometry service is external: projecting its criteria onto a fixed list drops the "
            + "ones we did not foresee");
        dto.Flags.Should().Equal("EXPIRED_DOCUMENT");
        dto.BreakdownMaximum.Should().BeNull(
            "the service reports a contribution per criterion and no denominator, so a per-criterion "
            + "gauge cannot be drawn honestly");
    }

    // ------------------------------------------------------------------ nothing sensitive

    [Fact]
    public async Task The_payload_carries_no_document_number_ocr_value_or_raw_zone()
    {
        var file = SeedFile();
        SeedAssessment(file.Id, 70, KycConfidenceLevel.Medium, "VERIFICATION");
        SeedFace(file.Id, 1, 0.9, true);

        _db.KycIdentityDocuments.Add(KycIdentityDocument.Create(
            tenantId: _tenantId, kycFileId: file.Id, docType: "CNI",
            encryptedNumber: "ciphertext", numberBlindIndex: "blind", clock: _clock,
            ocrFieldsJson: JsonSerializer.Serialize(new Dictionary<string, string> { ["surname"] = "OUATTARA" })));

        var json = JsonSerializer.Serialize(await ReadAsync(file.Id));

        // This is what lets the endpoint sit behind kyc:read: it describes the verdict, never the
        // identity. The OCR values and the number are the other slice's business.
        json.Should().NotContain("OUATTARA").And.NotContain("surname").And.NotContain("ciphertext");
    }

    // ------------------------------------------------------------------ absence and robustness

    [Fact]
    public async Task A_file_never_verified_reports_an_empty_verification_rather_than_an_error()
    {
        var file = SeedFile();

        var dto = await ReadAsync(file.Id);

        dto.ConfidenceScore.Should().BeNull();
        dto.Face.Should().BeNull();
        dto.Breakdown.Should().BeEmpty();
        dto.Flags.Should().BeEmpty();
        dto.FileStatus.Should().Be(nameof(KycFileStatus.Collecting),
            "the panel still has something true to show: where the file is");
    }

    [Fact]
    public async Task An_unknown_file_is_not_found()
    {
        var result = await Handler().Handle(new GetKycVerificationQuery(Guid.NewGuid()), default);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Be(KycErrors.FileNotFound);
    }

    [Fact]
    public async Task A_malformed_breakdown_omits_that_section_and_keeps_the_score()
    {
        var file = SeedFile();
        SeedAssessment(file.Id, 77, KycConfidenceLevel.High, "VERIFICATION",
            breakdownJson: "{ not json at all");

        var dto = await ReadAsync(file.Id);

        dto.ConfidenceScore.Should().Be(77);
        dto.Breakdown.Should().BeEmpty();
    }

    /// <summary>
    /// Advancing clock, so two assessments can be ordered. <c>TimeProvider.System</c> would record
    /// both inside one tick and make the "latest" assertion pass or fail by luck.
    /// </summary>
    private sealed class FakeTimeProvider : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 10, 1, 9, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan by) => _now = _now.Add(by);
    }
}
