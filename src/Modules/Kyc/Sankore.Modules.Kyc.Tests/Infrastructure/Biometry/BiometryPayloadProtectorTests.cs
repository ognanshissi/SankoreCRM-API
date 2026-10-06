namespace Sankore.Modules.Kyc.Tests.Infrastructure.Biometry;

using System.Security.Cryptography;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Sankore.Modules.Kyc.Infrastructure.Biometry;
using Sankore.Shared.Infrastructure.Crypto;
using Xunit;
using Generated = Sankore.Modules.Kyc.Infrastructure.Biometry.Generated;

/// <summary>
/// The service's own answers, kept so a corrected file can be re-scored.
///
/// <para>
/// The encryptor is the REAL <see cref="AesGcmFieldEncryptor"/> on a throw-away key: the central
/// property here is that the stored payload carries no plaintext, and a stub that prefixed the
/// string would make that assertion prove nothing.
/// </para>
/// </summary>
public sealed class BiometryPayloadProtectorTests
{
    private const string DocumentNumber = "CI0012345678";

    private readonly IFieldEncryptor _encryptor = new AesGcmFieldEncryptor(
        Options.Create(new FieldProtectionOptions
        {
            SectionName = "Kyc",
            FieldEncryptionKey = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)),
            BlindIndexKey = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)),
        }));

    private BiometryPayloadProtector Protector() =>
        new(_encryptor, NullLogger<BiometryPayloadProtector>.Instance);

    private static Generated.OcrResponse Ocr() => new()
    {
        Doc_type = Generated.DocType.CNI,
        Fields = new Dictionary<string, Generated.FieldValue>
        {
            ["surname"] = new() { Value = "OUATTARA", Confidence = 0.98, Source = "VISUAL" },
            ["document_number"] = new() { Value = DocumentNumber, Confidence = 0.99, Source = "MRZ" },
        },
        Mrz = new Generated.MrzData
        {
            Format = "TD1",
            Checksums_valid = true,
            Checks = [],
            Document_code = "ID",
            Issuing_country = "CIV",
            Document_number = DocumentNumber,
            Nationality = "CIV",
            Last_name = "OUATTARA",
            First_name = "AWA",
            Sex = "F",
        },
        Anomalies = [],
        Quality = new Generated.QualityInfo { Score = 0.82, Preprocessed = true },
        Model_versions = new Generated.ModelVersions { Service = "biometry-2.4.1" },
    };

    [Fact]
    public void The_stored_payload_never_carries_the_document_number_in_clear()
    {
        // This is the whole reason the column is encrypted rather than jsonb like its neighbours:
        // the service's answer repeats the number in `fields` AND in `mrz`, which is the one value
        // the row next to it exists to protect. OcrFieldsJson has it stripped for the same reason.
        var ciphertext = Protector().Protect(Ocr());

        ciphertext.Should().NotBeNull().And.StartWith("v1:");
        ciphertext.Should().NotContain(DocumentNumber);
        ciphertext.Should().NotContain("OUATTARA");
    }

    [Fact]
    public void A_payload_survives_the_round_trip_whole()
    {
        // Whole, not projected: the anomalies, the quality and the MRZ checks are what the score is
        // computed from, and they are exactly what this module's own columns drop.
        var protector = Protector();

        var restored = protector.UnprotectOcr(protector.Protect(Ocr()));

        restored.Should().NotBeNull();
        restored!.Fields["document_number"].Value.Should().Be(DocumentNumber);
        restored.Mrz!.Checksums_valid.Should().BeTrue();
        restored.Quality.Score.Should().Be(0.82);
        restored.Model_versions.Service.Should().Be("biometry-2.4.1");
    }

    [Fact]
    public void A_correction_is_applied_inside_the_payload_the_scorer_re_reads()
    {
        var payload = Ocr();

        BiometryPayloadProtector.ApplyCorrection(payload, "surname", "KOUASSI");

        payload.Fields["surname"].Value.Should().Be("KOUASSI");

        // An agent read it off the document with their own eyes, which beats the model's guess —
        // and the source says it was corrected, which is what the service penalises.
        payload.Fields["surname"].Confidence.Should().Be(1);
        payload.Fields["surname"].Low_confidence.Should().BeFalse();
        payload.Fields["surname"].Source.Should().Be("AGENT");

        payload.Fields["document_number"].Value.Should().Be(DocumentNumber,
            "no other field moves");
    }

    [Fact]
    public void A_field_the_machine_never_read_can_still_be_corrected()
    {
        var payload = Ocr();

        BiometryPayloadProtector.ApplyCorrection(payload, "place_of_birth", "BOUAKE");

        payload.Fields["place_of_birth"].Value.Should().Be("BOUAKE");
        payload.Fields["place_of_birth"].Source.Should().Be("AGENT");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-a-payload")]
    [InlineData("v1:aaaa:bbbb:cccc")]
    public void Anything_unreadable_comes_back_as_no_payload_rather_than_as_an_exception(string? stored)
    {
        // A file verified before the column existed, a rotated key, a shape the DTO has outgrown:
        // the caller then refuses to re-score, which is the honest answer. Throwing here would turn
        // a field correction — which succeeded — into a 500.
        Protector().UnprotectOcr(stored).Should().BeNull();
        Protector().UnprotectFace(stored).Should().BeNull();
    }
}
