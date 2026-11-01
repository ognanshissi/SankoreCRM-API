namespace Sankore.Modules.Kyc.Tests.Features.Verification;

using FluentAssertions;
using Sankore.Modules.Kyc.Features.Verification;
using Xunit;

/// <summary>
/// The field names the biometry service really sends (`app/services/ocr_service.py`,
/// REQUIRED_FIELDS). `doc_number` was not recognised: a passport read correctly, MRZ included,
/// still came back DOCUMENT_UNREADABLE (KYC recette, 2026-11-01).
/// </summary>
public sealed class KycOcrFieldsTests
{
    [Theory]
    [InlineData("doc_number")]
    [InlineData("document_number")]
    [InlineData("documentNumber")]
    [InlineData("passport_number")]
    public void The_document_number_is_found_under_the_services_spellings(string key)
        => KycOcrFields.FindDocumentNumber(new Dictionary<string, string>
        {
            ["last_name"] = "BAZIE",
            [key] = "A3247091",
        }).Should().Be("A3247091");

    [Fact]
    public void The_number_is_removed_from_what_is_stored_in_clear()
        => KycOcrFields.WithoutDocumentNumber(new Dictionary<string, string>
        {
            ["last_name"] = "BAZIE",
            ["doc_number"] = "A3247091",
        }).Keys.Should().Equal("last_name");
}
