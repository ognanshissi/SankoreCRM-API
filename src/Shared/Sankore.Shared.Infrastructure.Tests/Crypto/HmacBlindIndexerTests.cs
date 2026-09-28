namespace Sankore.Shared.Infrastructure.Tests.Crypto;

using System.Security.Cryptography;
using FluentAssertions;
using Microsoft.Extensions.Options;
using Sankore.Shared.Infrastructure.Crypto;
using Xunit;

public sealed class HmacBlindIndexerTests
{
    private sealed class FixedOptions<T>(T value) : IOptions<T> where T : class
    {
        public T Value { get; } = value;
    }

    private const string KeyA = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=";  // 32 zero-ish bytes
    private const string KeyB = "//////////////////////////////////////////8=";

    private static HmacBlindIndexer BuildIndexer(string keyBase64 = KeyA)
        => new(new FixedOptions<FieldProtectionOptions>(new FieldProtectionOptions
        {
            FieldEncryptionKey = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)),
            BlindIndexKey = keyBase64
        }));

    // ── S1: deterministic, lower-case hex ──────────────────────────────

    [Fact]
    public void Same_input_yields_the_same_lowercase_hex_index()
    {
        var indexer = BuildIndexer();

        var first = indexer.Compute(BlindIndexPurpose.Email, "alice@gmail.com");
        var second = indexer.Compute(BlindIndexPurpose.Email, "alice@gmail.com");

        first.Should().Be(second);
        first.Should().HaveLength(64);
        first.Should().MatchRegex("^[0-9a-f]{64}$");
    }

    // ── S2: domain separation between purposes ─────────────────────────

    [Fact]
    public void Different_purposes_yield_different_indexes_for_the_same_value()
    {
        var indexer = BuildIndexer();

        var asDocument = indexer.Compute(BlindIndexPurpose.IdentityDocument, "CI12345642");
        var asRegistration = indexer.Compute(BlindIndexPurpose.RegistrationNumber, "CI12345642");

        asRegistration.Should().NotBe(asDocument);
    }

    // ── S3: phone normalization — indicatif KEPT, trunk prefix neutralised ──

    [Fact]
    public void Phone_written_with_plus_or_double_zero_yields_one_index()
    {
        var indexer = BuildIndexer();

        var plus = indexer.Compute(BlindIndexPurpose.Phone, "+225 07 08 09 18 01");
        var doubleZero = indexer.Compute(BlindIndexPurpose.Phone, "00225 07 08 09 18 01");
        var punctuated = indexer.Compute(BlindIndexPurpose.Phone, "(+225) 07-08.09.18-01");

        doubleZero.Should().Be(plus);
        punctuated.Should().Be(plus);
    }

    [Fact]
    public void French_trunk_zero_after_the_calling_code_is_neutralised()
    {
        // The two forms designate the SAME French subscriber.
        var indexer = BuildIndexer();

        indexer.Compute(BlindIndexPurpose.Phone, "+330799887766")
            .Should().Be(indexer.Compute(BlindIndexPurpose.Phone, "+33799887766"));

        SensitiveValueNormalizer.NormalizePhone("+33 0 7 99 88 77 66").Should().Be("33799887766");
        SensitiveValueNormalizer.NormalizePhone("0033799887766").Should().Be("33799887766");
    }

    [Fact]
    public void Ivorian_leading_zero_is_significant_and_never_stripped()
    {
        // Côte d'Ivoire has had no trunk prefix since the 2021 10-digit migration:
        // the leading 0 belongs to the subscriber number.
        SensitiveValueNormalizer.NormalizePhone("+225 07 08 09 18 01").Should().Be("2250708091801");
    }

    [Fact]
    public void The_indicatif_is_kept_so_a_local_form_is_a_distinct_value()
    {
        // Deliberate: the indicatif is no longer inferred, so a number captured
        // locally does not collide with the same subscriber written internationally.
        var indexer = BuildIndexer();

        indexer.Compute(BlindIndexPurpose.Phone, "07 08 09 18 01")
            .Should().NotBe(indexer.Compute(BlindIndexPurpose.Phone, "+225 07 08 09 18 01"));

        SensitiveValueNormalizer.NormalizePhone("07 08 09 18 01").Should().Be("0708091801");
    }

    [Fact]
    public void A_local_number_keeps_digits_that_look_like_a_calling_code()
    {
        // "25 07 08 09 18" is an Abidjan land line, NOT the Rwandan code 250.
        SensitiveValueNormalizer.NormalizePhone("25 07 08 09 18").Should().Be("2507080918");
    }

    [Fact]
    public void Different_phones_yield_different_indexes()
    {
        var indexer = BuildIndexer();

        indexer.Compute(BlindIndexPurpose.Phone, "07 08 09 18")
            .Should().NotBe(indexer.Compute(BlindIndexPurpose.Phone, "07 08 09 19"));
    }

    // ── S4: email case / whitespace equivalence ────────────────────────

    [Fact]
    public void Email_case_and_padding_are_irrelevant()
    {
        var indexer = BuildIndexer();

        var canonical = indexer.Compute(BlindIndexPurpose.Email, "alice@gmail.com");

        indexer.Compute(BlindIndexPurpose.Email, "  ALICE@Gmail.COM  ").Should().Be(canonical);
    }

    // ── S5: document punctuation / case equivalence ────────────────────

    [Fact]
    public void Document_number_punctuation_and_case_are_irrelevant()
    {
        var indexer = BuildIndexer();

        var canonical = indexer.Compute(BlindIndexPurpose.IdentityDocument, "CI12345642");

        indexer.Compute(BlindIndexPurpose.IdentityDocument, " ci-1234 56.42 ").Should().Be(canonical);
    }

    [Fact]
    public void Date_of_birth_accepts_both_iso_and_slashed_forms()
    {
        var indexer = BuildIndexer();

        var canonical = indexer.Compute(BlindIndexPurpose.DateOfBirth, "1987-04-02");

        indexer.Compute(BlindIndexPurpose.DateOfBirth, "02/04/1987").Should().Be(canonical);
        indexer.Compute(
                BlindIndexPurpose.DateOfBirth,
                SensitiveValueNormalizer.NormalizeDateOfBirth(new DateOnly(1987, 4, 2)))
            .Should().Be(canonical);
    }

    [Fact]
    public void Postal_address_accents_punctuation_and_case_are_irrelevant()
    {
        var indexer = BuildIndexer();

        var canonical = indexer.Compute(BlindIndexPurpose.PostalAddress, "COCODY ANGRE 7EME TRANCHE");

        indexer.Compute(BlindIndexPurpose.PostalAddress, "  Cocody, Angré   7ème  tranche. ")
            .Should().Be(canonical);
    }

    // ── S6: the key matters ────────────────────────────────────────────

    [Fact]
    public void Different_keys_yield_different_indexes()
    {
        var withKeyA = BuildIndexer(KeyA).Compute(BlindIndexPurpose.Phone, "+225 07 08 09 18");
        var withKeyB = BuildIndexer(KeyB).Compute(BlindIndexPurpose.Phone, "+225 07 08 09 18");

        withKeyB.Should().NotBe(withKeyA);
    }

    [Fact]
    public void Short_key_throws_with_generation_guidance()
    {
        var indexer = BuildIndexer(Convert.ToBase64String(new byte[16]));

        var act = () => indexer.Compute(BlindIndexPurpose.Phone, "0708091812");

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*Customers:BlindIndexKey*")
            .WithMessage("*RandomNumberGenerator.GetBytes(32)*");
    }
}
