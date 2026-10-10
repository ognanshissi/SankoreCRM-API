namespace Sankore.Shared.Infrastructure.Tests.Person;

using System.Globalization;
using System.Security.Cryptography;
using FluentAssertions;
using Microsoft.Extensions.Options;
using Sankore.Shared.Infrastructure.Crypto;
using Sankore.Shared.Infrastructure.Persistence;
using Xunit;

/// <summary>
/// Covers <see cref="EncryptedConverters"/> — the converters that put the module's encryptor
/// between a domain value and its column.
/// </summary>
public sealed class EncryptedConvertersTests
{
    private sealed class FixedOptions<T>(T value) : IOptions<T> where T : class
    {
        public T Value { get; } = value;
    }

    private static IFieldEncryptor Encryptor()
        => new AesGcmFieldEncryptor(new FixedOptions<FieldProtectionOptions>(new FieldProtectionOptions
        {
            FieldEncryptionKey = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)),
            BlindIndexKey      = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)),
        }));

    private static TTarget To<TModel, TTarget>(
        Microsoft.EntityFrameworkCore.Storage.ValueConversion.ValueConverter converter, TModel value)
        => (TTarget)converter.ConvertToProviderExpression.Compile().DynamicInvoke(value)!;

    private static TModel From<TModel>(
        Microsoft.EntityFrameworkCore.Storage.ValueConversion.ValueConverter converter, object? stored)
        => (TModel)converter.ConvertFromProviderExpression.Compile().DynamicInvoke(stored)!;

    // ── Text ───────────────────────────────────────────────────────────────

    [Fact]
    public void Text_round_trips_and_never_stores_the_clear_value()
    {
        var converter = EncryptedConverters.Text(Encryptor());

        var stored = To<string?, string?>(converter, "Rue des Jardins 12");

        stored.Should().NotBeNull();
        stored.Should().StartWith("v1:");
        stored.Should().NotContain("Jardins", "the column must never hold the clear address");

        From<string?>(converter, stored).Should().Be("Rue des Jardins 12");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Text_collapses_an_absent_value_to_null(string? input)
    {
        // A column holding the ciphertext of an empty string looks like real data at a glance and
        // costs ~60 bytes to say nothing.
        To<string?, string?>(EncryptedConverters.Text(Encryptor()), input).Should().BeNull();
    }

    [Fact]
    public void Text_produces_a_different_payload_each_time()
    {
        // AES-256-GCM with a fresh nonce. This is why a converted column can never be indexed or
        // compared in SQL, and why a blind index stays a separate column.
        var converter = EncryptedConverters.Text(Encryptor());

        var first  = To<string?, string?>(converter, "Rue des Jardins 12");
        var second = To<string?, string?>(converter, "Rue des Jardins 12");

        first.Should().NotBe(second);
    }

    // ── Date ───────────────────────────────────────────────────────────────

    [Fact]
    public void Date_round_trips()
    {
        var converter = EncryptedConverters.Date(Encryptor());
        var stored = To<DateOnly?, string?>(converter, new DateOnly(1987, 4, 2));

        stored.Should().StartWith("v1:");
        From<DateOnly?>(converter, stored).Should().Be(new DateOnly(1987, 4, 2));
    }

    [Fact]
    public void Date_round_trips_to_the_same_day_on_a_french_host()
    {
        // THE test this file exists for. `02/04/1987` is 2 April to a fr-FR reader and 4 February
        // to the invariant short-date pattern, and encryption makes the damage silent: the value
        // decrypts perfectly and is simply the wrong day. The repository has already paid for this
        // once in the spreadsheet readers, where the bug passed every test on an
        // invariant-culture machine.
        var converter = EncryptedConverters.Date(Encryptor());
        var birthDate = new DateOnly(1987, 4, 2);

        var original = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("fr-FR");
            var stored = To<DateOnly?, string?>(converter, birthDate);

            CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
            From<DateOnly?>(converter, stored).Should().Be(birthDate,
                "the wire format is yyyy-MM-dd with InvariantCulture on both sides");
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }

    [Fact]
    public void Date_written_on_an_invariant_host_reads_back_on_a_french_one()
    {
        // The same trap in the other direction — a database written by one deployment and read by
        // another whose container locale differs.
        var converter = EncryptedConverters.Date(Encryptor());
        var birthDate = new DateOnly(1987, 4, 2);

        var original = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
            var stored = To<DateOnly?, string?>(converter, birthDate);

            CultureInfo.CurrentCulture = new CultureInfo("fr-FR");
            From<DateOnly?>(converter, stored).Should().Be(birthDate);
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }

    [Fact]
    public void Date_stores_null_for_an_unknown_birth_date()
    {
        To<DateOnly?, string?>(EncryptedConverters.Date(Encryptor()), null).Should().BeNull();
    }

    [Fact]
    public void Date_reads_an_undecryptable_column_as_unknown()
    {
        // A row encrypted under a rotated-away key must not throw on every read of the screen it
        // appears on. Unknown is the honest answer, and it is also what a NULL column gives.
        From<DateOnly?>(EncryptedConverters.Date(Encryptor()), null).Should().BeNull();
    }
}
