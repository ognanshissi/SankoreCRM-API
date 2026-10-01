namespace Sankore.Modules.Kyc.Tests.Features.Corrections;

using System.Security.Cryptography;
using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Sankore.Modules.Kyc.Domain;
using Sankore.Modules.Kyc.Features.Corrections.CorrectField;
using Sankore.Modules.Kyc.Infrastructure;
using Sankore.Modules.Kyc.Infrastructure.Biometry;
using Sankore.Modules.Kyc.Tests.TestSupport;
using Sankore.Shared.Infrastructure.Crypto;
using Xunit;

/// <summary>
/// KYC-B-04, correction side.
///
/// The encryptor is the REAL <see cref="AesGcmFieldEncryptor"/> on a throw-away key, not a stub
/// that prefixes the string: the central assertion of this suite is that no plaintext survives
/// into the corrections table, and a fake encryptor would make that assertion prove nothing.
/// </summary>
public sealed class CorrectKycFieldHandlerTests : IDisposable
{
    private const string Misread = "OUATARA";
    private const string Corrected = "OUATTARA";

    private readonly Guid _tenantId = Guid.NewGuid();
    private readonly Guid _agentId = Guid.NewGuid();
    private readonly TestKycDbContextFactory _factory;
    private readonly KycDbContext _db;
    private readonly FakeBiometryClient _biometry = new();
    private readonly IFieldEncryptor _encryptor;
    private readonly TimeProvider _clock = TimeProvider.System;

    public CorrectKycFieldHandlerTests()
    {
        _factory = new TestKycDbContextFactory(_tenantId);
        _db = _factory.CreateContext();

        var options = Options.Create(new FieldProtectionOptions
        {
            SectionName = "Kyc",
            FieldEncryptionKey = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)),
            BlindIndexKey = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)),
        });

        _encryptor = new AesGcmFieldEncryptor(options);
    }

    public void Dispose()
    {
        _db.Dispose();
        _factory.Dispose();
    }

    private CorrectKycFieldHandler Handler() => new(_db, _clock, _biometry, _encryptor);

    private async Task<KycFile> SeedFileAsync(bool withDocument = true)
    {
        var file = KycFile.Open(_tenantId, Guid.NewGuid(), KycChannel.Agency, _agentId, _clock);
        _db.KycFiles.Add(file);

        if (withDocument)
        {
            _db.KycIdentityDocuments.Add(KycIdentityDocument.Create(
                tenantId: _tenantId,
                kycFileId: file.Id,
                docType: "CNI",
                encryptedNumber: _encryptor.Encrypt("CI0012345678")!,
                numberBlindIndex: "some-blind-index",
                clock: _clock,
                ocrFieldsJson: JsonSerializer.Serialize(new Dictionary<string, string>
                {
                    ["surname"] = Misread,
                    ["given_names"] = "AWA",
                }),
                serviceVersion: "flask-1.2.3"));
        }

        await _db.SaveChangesAsync();
        return file;
    }

    private CorrectKycFieldCommand Command(Guid fileId, string source = "OCR") =>
        new(fileId, "surname", source, Corrected, _agentId);

    [Fact]
    public async Task A_correction_records_one_row_with_the_field_name_its_source_and_its_author()
    {
        var file = await SeedFileAsync();

        var result = await Handler().Handle(Command(file.Id), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();

        var correction = (await _db.KycFieldCorrections.ToListAsync()).Should().ContainSingle().Subject;
        correction.KycFileId.Should().Be(file.Id);
        correction.FieldName.Should().Be("surname");
        correction.Source.Should().Be("OCR");
        correction.CorrectedBy.Should().Be(_agentId);
        correction.Id.Should().Be(result.Value.CorrectionId);
    }

    [Fact]
    public async Task Neither_the_previous_nor_the_new_value_survives_in_clear()
    {
        // The reason both columns are encrypted: "previous = OUATARA, new = OUATTARA" in a
        // corrections table IS the name, and would undo the encryption of the document itself.
        var file = await SeedFileAsync();

        await Handler().Handle(Command(file.Id), CancellationToken.None);

        var correction = await _db.KycFieldCorrections.SingleAsync();
        var serialized = JsonSerializer.Serialize(correction);

        serialized.Should().NotContain(Misread);
        serialized.Should().NotContain(Corrected);

        // And the payloads are genuinely the values, not nulls that would pass the test above.
        _encryptor.Decrypt(correction.EncryptedPreviousValue!).Should().Be(Misread);
        _encryptor.Decrypt(correction.EncryptedNewValue).Should().Be(Corrected);
    }

    [Fact]
    public async Task A_correction_re_scores_the_file_without_ever_re_running_the_OCR()
    {
        // The acceptance criterion, and the reason the handler injects IBiometryClient instead of
        // calling the verification slice: re-running the OCR would overwrite the agent's
        // correction with the same misreading that caused it.
        var file = await SeedFileAsync();

        await Handler().Handle(Command(file.Id), CancellationToken.None);

        _biometry.Calls.Select(c => c.Endpoint).Should().ContainSingle().Which.Should().Be("score");
        _biometry.Calls.Should().NotContain(c => c.Endpoint == "ocr");
        _biometry.Calls.Should().NotContain(c => c.Endpoint == "face-match");
    }

    [Fact]
    public async Task The_new_score_is_appended_as_an_assessment_triggered_by_the_correction()
    {
        var file = await SeedFileAsync();
        _biometry.Score = 91;

        var result = await Handler().Handle(Command(file.Id), CancellationToken.None);

        var assessment = (await _db.KycConfidenceAssessments.ToListAsync())
            .Should().ContainSingle().Subject;

        assessment.KycFileId.Should().Be(file.Id);
        assessment.Trigger.Should().Be("FIELD_CORRECTION");
        assessment.GlobalScore.Should().Be(91);
        assessment.Level.Should().Be(KycConfidenceLevel.High);
        result.Value.NewScore.Should().Be(91);
        result.Value.ScoreUnavailableCode.Should().BeNull();
    }

    [Fact]
    public async Task The_scorer_is_handed_the_corrected_value_not_the_misread_one()
    {
        var file = await SeedFileAsync();

        await Handler().Handle(Command(file.Id), CancellationToken.None);

        _biometry.LastScoreRequest!.Ocr!.Fields["surname"].Should().Be(Corrected);
        _biometry.LastScoreRequest.DocumentType.Should().Be("CNI");
    }

    [Fact]
    public async Task The_corrected_value_replaces_the_stored_OCR_field()
    {
        var file = await SeedFileAsync();

        await Handler().Handle(Command(file.Id), CancellationToken.None);

        var document = await _db.KycIdentityDocuments.AsNoTracking().SingleAsync();
        var fields = JsonSerializer.Deserialize<Dictionary<string, string>>(document.OcrFieldsJson!)!;

        fields["surname"].Should().Be(Corrected);
        // Untouched fields stay: a correction is a field edit, not a re-upload of the reading.
        fields["given_names"].Should().Be("AWA");
    }

    [Fact]
    public async Task An_unreachable_scorer_does_not_lose_the_correction()
    {
        // The correction is the agent's own factual statement about the document. Rolling it back
        // because a Flask deployment is down would simply make them type it again.
        var file = await SeedFileAsync();
        var handler = new CorrectKycFieldHandler(
            _db, _clock, FakeBiometryClient.Unavailable(), _encryptor);

        var result = await handler.Handle(Command(file.Id), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.NewScore.Should().BeNull();
        result.Value.ScoreUnavailableCode.Should().Be(BiometryCodes.ServiceUnavailable);

        (await _db.KycFieldCorrections.ToListAsync()).Should().ContainSingle();
        (await _db.KycConfidenceAssessments.ToListAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task A_file_that_carries_no_identity_document_has_nothing_to_correct()
    {
        var file = await SeedFileAsync(withDocument: false);

        var result = await Handler().Handle(Command(file.Id), CancellationToken.None);

        result.Error.Should().Be("KYC_IDENTITY_DOCUMENT_NOT_FOUND");
        (await _db.KycFieldCorrections.ToListAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task An_unknown_file_is_reported_not_found()
    {
        await SeedFileAsync();

        var result = await Handler().Handle(Command(Guid.NewGuid()), CancellationToken.None);

        result.Error.Should().Be(KycErrors.FileNotFound);
    }

    [Fact]
    public async Task A_correction_attributed_to_an_unknown_reading_is_refused()
    {
        var file = await SeedFileAsync();

        var result = await Handler().Handle(Command(file.Id, source: "GUESS"), CancellationToken.None);

        result.Error.Should().Be("KYC_CORRECTION_SOURCE_INVALID");
        (await _db.KycFieldCorrections.ToListAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task A_correction_attributed_to_the_MRZ_is_recorded_as_such()
    {
        // Source is provenance, not a target column: the corrected value still lands in the OCR
        // field set, which is what the scorer reads. MrzDataJson stays as the machine read it.
        var file = await SeedFileAsync();

        await Handler().Handle(Command(file.Id, source: "mrz"), CancellationToken.None);

        (await _db.KycFieldCorrections.SingleAsync()).Source.Should().Be("MRZ");
    }
}
