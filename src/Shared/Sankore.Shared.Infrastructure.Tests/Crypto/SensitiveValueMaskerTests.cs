namespace Sankore.Shared.Infrastructure.Tests.Crypto;

using FluentAssertions;
using Sankore.Shared.Infrastructure.Crypto;
using Xunit;

public sealed class SensitiveValueMaskerTests
{
    // ── Documents ──────────────────────────────────────────────────────

    [Fact]
    public void Masks_a_document_number_keeping_two_characters_on_each_side()
        => SensitiveValueMasker.MaskDocument("CI12345642").Should().Be("CI•••••••42");

    [Fact]
    public void Fully_masks_a_very_short_document_number()
        => SensitiveValueMasker.MaskDocument("CI42").Should().Be("••••");

    // ── Phones ─────────────────────────────────────────────────────────

    [Fact]
    public void Masks_an_international_phone_keeping_the_calling_code_and_last_two_digits()
        => SensitiveValueMasker.MaskPhone("+22507080918").Should().Be("+225 07 •• •• 18");

    [Fact]
    public void Masks_a_local_phone_without_a_calling_code()
        => SensitiveValueMasker.MaskPhone("07080918").Should().Be("07 •• •• 18");

    // ── Emails ─────────────────────────────────────────────────────────

    [Fact]
    public void Masks_an_email_keeping_the_initials_and_the_tld()
        => SensitiveValueMasker.MaskEmail("alice@gmail.com").Should().Be("a•••@g•••.com");

    [Fact]
    public void Falls_back_to_generic_masking_when_the_value_is_not_an_email()
        => SensitiveValueMasker.MaskEmail("not-an-email").Should().Be("n•••l");

    // ── Generic ────────────────────────────────────────────────────────

    [Fact]
    public void Masks_a_generic_value_keeping_first_and_last_character()
        => SensitiveValueMasker.MaskGeneric("Abidjan").Should().Be("A•••n");

    [Fact]
    public void Fully_masks_a_very_short_generic_value()
        => SensitiveValueMasker.MaskGeneric("ABC").Should().Be("••••");

    // ── Dates ──────────────────────────────────────────────────────────

    [Fact]
    public void Masks_a_date_of_birth_keeping_only_the_year()
        => SensitiveValueMasker.MaskDate(new DateOnly(1987, 4, 2)).Should().Be("••/••/1987");

    // ── Null / blank handling ──────────────────────────────────────────

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Every_masker_returns_null_for_null_or_blank(string? input)
    {
        SensitiveValueMasker.MaskDocument(input).Should().BeNull();
        SensitiveValueMasker.MaskPhone(input).Should().BeNull();
        SensitiveValueMasker.MaskEmail(input).Should().BeNull();
        SensitiveValueMasker.MaskGeneric(input).Should().BeNull();
    }

    [Fact]
    public void Mask_date_returns_null_for_null()
        => SensitiveValueMasker.MaskDate(null).Should().BeNull();
}
