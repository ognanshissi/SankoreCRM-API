namespace Sankore.Modules.Kyc.Tests.Features.Documents;

using System.Security.Cryptography;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Sankore.Modules.Kyc.Domain;
using Sankore.Modules.Kyc.Features.Documents.GetIdentityDocument;
using Sankore.Modules.Kyc.Infrastructure;
using Sankore.Modules.Kyc.Tests.TestSupport;
using Sankore.Shared.Infrastructure.Crypto;
using Xunit;

/// <summary>
/// The OCR panel's read side. What is pinned here is the compromise the owner chose: an agent sees
/// enough to recognise the document and correct a misreading, and not enough for this to become a
/// way to read the number. The encryptor is the REAL one on a throw-away key — a stub would prove
/// the masking is applied to something, not that it is applied to the decrypted number.
/// </summary>
public sealed class GetKycIdentityDocumentHandlerTests : IDisposable
{
    private const string Number = "CI0012345678";

    private readonly Guid _tenantId = Guid.NewGuid();
    private readonly TestKycDbContextFactory _factory;
    private readonly KycDbContext _db;
    private readonly IFieldEncryptor _encryptor;
    private readonly TimeProvider _clock = TimeProvider.System;

    public GetKycIdentityDocumentHandlerTests()
    {
        _factory = new TestKycDbContextFactory(_tenantId);
        _db = _factory.CreateContext();

        _encryptor = new AesGcmFieldEncryptor(Options.Create(new FieldProtectionOptions
        {
            SectionName = "Kyc",
            FieldEncryptionKey = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)),
            BlindIndexKey = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)),
        }));
    }

    public void Dispose()
    {
        _db.Dispose();
        _factory.Dispose();
    }

    private GetKycIdentityDocumentHandler Handler(IFieldEncryptor? encryptor = null) =>
        new(_db, encryptor ?? _encryptor, _clock,
            NullLogger<GetKycIdentityDocumentHandler>.Instance);

    private async Task<KycFile> SeedAsync(
        bool withDocument = true,
        string? number = Number,
        DateOnly? expiry = null,
        bool withConfidences = true,
        bool withMrz = true,
        string? malformedOcrJson = null)
    {
        var file = KycFile.Open(_tenantId, Guid.NewGuid(), KycChannel.Agency, Guid.NewGuid(), _clock);
        _db.KycFiles.Add(file);

        if (withDocument)
        {
            _db.KycIdentityDocuments.Add(KycIdentityDocument.Create(
                tenantId: _tenantId,
                kycFileId: file.Id,
                docType: "CNI",
                encryptedNumber: _encryptor.Encrypt(number)!,
                numberBlindIndex: "blind-index",
                clock: _clock,
                expiryDate: expiry,
                // The number is absent, as RunKycVerificationHandler strips it before writing.
                ocrFieldsJson: malformedOcrJson ?? JsonSerializer.Serialize(new Dictionary<string, string>
                {
                    ["surname"] = "OUATTARA",
                    ["given_names"] = "AWA",
                }),
                mrzDataJson: withMrz
                    ? JsonSerializer.Serialize(new { checksumValid = true, fields = new Dictionary<string, string> { ["surname"] = "OUATTARA" } })
                    : null,
                serviceVersion: "flask-1.2.3",
                ocrFieldConfidencesJson: withConfidences
                    ? JsonSerializer.Serialize(new Dictionary<string, double> { ["surname"] = 0.97, ["given_names"] = 0.41 })
                    : null));
        }

        await _db.SaveChangesAsync();
        return file;
    }

    // ------------------------------------------------------------------ the masking compromise

    [Fact]
    public async Task The_number_comes_back_masked_and_the_clear_value_appears_nowhere()
    {
        var file = await SeedAsync();

        var result = await Handler().Handle(new GetKycIdentityDocumentQuery(file.Id), default);

        result.IsSuccess.Should().BeTrue();
        result.Value!.MaskedNumber.Should().Be("CI•••••••78");

        // The whole payload, not just that field: a number echoed into an OCR value or an MRZ field
        // would defeat the masking while this assertion still passed on MaskedNumber alone.
        JsonSerializer.Serialize(result.Value).Should().NotContain(Number);
    }

    [Fact]
    public async Task The_masked_number_still_identifies_which_document_is_on_screen()
    {
        var file = await SeedAsync();

        var masked = (await Handler().Handle(new GetKycIdentityDocumentQuery(file.Id), default)).Value!.MaskedNumber;

        // The point of masking rather than omitting: an agent correcting a reading has to know they
        // are looking at the right document.
        masked.Should().StartWith("CI").And.EndWith("78");
    }

    [Fact]
    public async Task A_number_that_cannot_be_decrypted_blanks_that_field_and_keeps_the_panel()
    {
        var file = await SeedAsync();

        // A rotated or wrong Kyc:FieldEncryptionKey.
        var otherKey = new AesGcmFieldEncryptor(Options.Create(new FieldProtectionOptions
        {
            SectionName = "Kyc",
            FieldEncryptionKey = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)),
            BlindIndexKey = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)),
        }));

        var result = await Handler(otherKey).Handle(new GetKycIdentityDocumentQuery(file.Id), default);

        result.IsSuccess.Should().BeTrue("the fields are what the agent came for and are still readable");
        result.Value!.MaskedNumber.Should().BeNull();
        result.Value.Fields.Should().ContainKey("surname");
    }

    // ------------------------------------------------------------------ what the panel renders

    [Fact]
    public async Task The_fields_and_their_confidences_come_back_keyed_alike()
    {
        var file = await SeedAsync();

        var dto = (await Handler().Handle(new GetKycIdentityDocumentQuery(file.Id), default)).Value!;

        dto.Fields.Should().Contain(new KeyValuePair<string, string>("surname", "OUATTARA"));
        dto.FieldConfidences["given_names"].Should().BeApproximately(0.41, 0.001,
            "a low confidence is exactly what sends the agent to check that field");
        dto.FieldConfidences.Keys.Should().BeSubsetOf(dto.Fields.Keys,
            "a confidence for a field that is not rendered would name a field we removed");
    }

    [Fact]
    public async Task A_document_read_before_confidences_were_stored_reports_none_rather_than_certainty()
    {
        var file = await SeedAsync(withConfidences: false);

        var dto = (await Handler().Handle(new GetKycIdentityDocumentQuery(file.Id), default)).Value!;

        dto.FieldConfidences.Should().BeEmpty(
            "no backfill can invent them, so the caller must be able to tell absent from confident");
        dto.Fields.Should().NotBeEmpty();
    }

    [Fact]
    public async Task The_machine_readable_zone_comes_back_without_its_raw_line()
    {
        var file = await SeedAsync();

        var mrz = (await Handler().Handle(new GetKycIdentityDocumentQuery(file.Id), default)).Value!.Mrz;

        mrz.Should().NotBeNull();
        mrz!.ChecksumValid.Should().BeTrue();
        mrz.Fields.Should().ContainKey("surname");
        mrz.RawLine.Should().BeNull(
            "the raw line spells the document number out, so it is never stored in the first place");
    }

    [Fact]
    public async Task A_document_with_no_zone_reports_none()
    {
        var file = await SeedAsync(withMrz: false);

        (await Handler().Handle(new GetKycIdentityDocumentQuery(file.Id), default)).Value!.Mrz.Should().BeNull();
    }

    [Theory]
    [InlineData("2020-01-01", true)]
    [InlineData("2099-01-01", false)]
    public async Task Expiry_is_reported_as_a_fact_rather_than_left_to_the_caller(string expiry, bool expected)
    {
        var file = await SeedAsync(expiry: DateOnly.Parse(expiry, System.Globalization.CultureInfo.InvariantCulture));

        var dto = (await Handler().Handle(new GetKycIdentityDocumentQuery(file.Id), default)).Value!;

        dto.IsExpired.Should().Be(expected);
    }

    // ------------------------------------------------------------------ absence and robustness

    [Fact]
    public async Task An_unknown_file_is_not_found()
    {
        var result = await Handler().Handle(new GetKycIdentityDocumentQuery(Guid.NewGuid()), default);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Be(KycErrors.FileNotFound);
    }

    [Fact]
    public async Task A_file_with_no_reading_yet_is_told_apart_from_an_unknown_one()
    {
        var file = await SeedAsync(withDocument: false);

        var result = await Handler().Handle(new GetKycIdentityDocumentQuery(file.Id), default);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Be(KycErrors.IdentityDocumentNotFound,
            "an empty panel and a wrong URL are different problems for whoever is looking at it");
    }

    [Fact]
    public async Task A_malformed_json_column_omits_that_section_instead_of_failing()
    {
        var file = await SeedAsync(malformedOcrJson: "{ this is not json");

        var result = await Handler().Handle(new GetKycIdentityDocumentQuery(file.Id), default);

        result.IsSuccess.Should().BeTrue();
        result.Value!.Fields.Should().BeEmpty();
        result.Value.MaskedNumber.Should().Be("CI•••••••78", "the rest of the document is still true");
    }
}
