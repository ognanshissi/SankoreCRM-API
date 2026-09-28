namespace Sankore.Shared.Infrastructure.Tests.Crypto;

using System.Security.Cryptography;
using FluentAssertions;
using Microsoft.Extensions.Options;
using Sankore.Shared.Infrastructure.Crypto;
using Xunit;

public sealed class AesGcmFieldEncryptorTests
{
    // ── Hand-rolled IOptions fake (no NSubstitute in this project) ──────

    private sealed class FixedOptions<T>(T value) : IOptions<T> where T : class
    {
        public T Value { get; } = value;
    }

    private static AesGcmFieldEncryptor BuildEncryptor(int keyBytes = 32)
        => new(new FixedOptions<FieldProtectionOptions>(new FieldProtectionOptions
        {
            FieldEncryptionKey = Convert.ToBase64String(RandomNumberGenerator.GetBytes(keyBytes)),
            BlindIndexKey = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
        }));

    // ── S1: round-trip ─────────────────────────────────────────────────

    [Fact]
    public void Round_trips_a_sensitive_value()
    {
        var encryptor = BuildEncryptor();

        var payload = encryptor.Encrypt("CI12345642");

        payload.Should().NotBeNull();
        payload.Should().StartWith("v1:");
        payload!.Split(':').Should().HaveCount(4);
        encryptor.Decrypt(payload).Should().Be("CI12345642");
    }

    [Fact]
    public void Round_trips_accented_and_long_values()
    {
        var encryptor = BuildEncryptor();
        const string clear = "Koffi N'Guessan Aké — Cocody, Abidjan (Côte d'Ivoire)";

        encryptor.Decrypt(encryptor.Encrypt(clear)).Should().Be(clear);
    }

    // ── S2: null / whitespace passthrough ──────────────────────────────

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Returns_null_for_null_or_whitespace(string? input)
    {
        var encryptor = BuildEncryptor();

        encryptor.Encrypt(input).Should().BeNull();
        encryptor.Decrypt(input).Should().BeNull();
    }

    // ── S3: random nonce → different ciphertexts, both decryptable ─────

    [Fact]
    public void Same_plaintext_encrypts_to_different_payloads_that_both_decrypt()
    {
        var encryptor = BuildEncryptor();

        var first = encryptor.Encrypt("0708091812");
        var second = encryptor.Encrypt("0708091812");

        second.Should().NotBe(first, "a fresh 12-byte nonce is drawn on every call");
        encryptor.Decrypt(first).Should().Be("0708091812");
        encryptor.Decrypt(second).Should().Be("0708091812");
    }

    // ── S4: tampering is detected by the GCM tag ───────────────────────

    [Fact]
    public void Tampered_ciphertext_throws()
    {
        var encryptor = BuildEncryptor();
        var parts = encryptor.Encrypt("CI12345642")!.Split(':');

        var cipherBytes = Convert.FromBase64String(parts[3]);
        cipherBytes[0] ^= 0xFF;
        var tampered = string.Join(':', parts[0], parts[1], parts[2], Convert.ToBase64String(cipherBytes));

        var act = () => encryptor.Decrypt(tampered);

        act.Should().Throw<CryptographicException>();
    }

    [Fact]
    public void Malformed_payload_throws_invalid_operation()
    {
        var encryptor = BuildEncryptor();

        var act = () => encryptor.Decrypt("not-a-protected-payload");

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*v1:nonce:tag:ciphertext*");
    }

    [Fact]
    public void Unknown_version_throws_invalid_operation()
    {
        var encryptor = BuildEncryptor();
        var parts = encryptor.Encrypt("CI12345642")!.Split(':');
        var v2 = string.Join(':', "v2", parts[1], parts[2], parts[3]);

        var act = () => encryptor.Decrypt(v2);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*version 'v2'*");
    }

    // ── S5: key misconfiguration is explained to the operator ──────────

    [Fact]
    public void Wrong_size_key_throws_with_generation_guidance()
    {
        var encryptor = BuildEncryptor(keyBytes: 16);

        var act = () => encryptor.Encrypt("CI12345642");

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*32 bytes*")
            .WithMessage("*RandomNumberGenerator.GetBytes(32)*");
    }

    [Fact]
    public void Missing_key_throws_with_generation_guidance()
    {
        var encryptor = new AesGcmFieldEncryptor(
            new FixedOptions<FieldProtectionOptions>(new FieldProtectionOptions
            {
                FieldEncryptionKey = "   ",
                BlindIndexKey = "   "
            }));

        var act = () => encryptor.Encrypt("CI12345642");

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*Customers:FieldEncryptionKey*")
            .WithMessage("*RandomNumberGenerator.GetBytes(32)*");
    }
}
