namespace Sankore.Shared.Infrastructure.Persistence;

using System.Globalization;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using Sankore.Shared.Infrastructure.Crypto;

/// <summary>
/// EF value converters that put an <see cref="IFieldEncryptor"/> between the domain value and the
/// column, so a handler cannot forget to encrypt.
///
/// <para>
/// This is the opposite trade-off from M01's <c>EncryptedDateOfBirth</c>-style properties, where
/// the aggregate holds ciphertext and every caller is responsible for producing it. That works,
/// and it is why those columns are safe today — but it puts the obligation on code that is written
/// once per feature, and the failure mode is a clear value in a column nobody looks at. A
/// converter moves the obligation into the mapping, which is written once per type.
/// </para>
///
/// <para>
/// <b>Two consequences to know before using these.</b> The ciphertext is non-deterministic
/// (AES-256-GCM with a fresh nonce), so a converted column can never be indexed, compared or
/// ordered in SQL — that is what a blind index is for, and it stays a separate column. And EF
/// compares the CLR value for change tracking, not the converted one, so an unchanged value does
/// not re-encrypt on every save.
/// </para>
/// </summary>
public static class EncryptedConverters
{
    /// <summary>
    /// Encrypted text. Null and blank both round-trip to null: a column holding the ciphertext of
    /// an empty string is indistinguishable from real data at a glance and costs ~60 bytes to say
    /// nothing.
    /// </summary>
    public static ValueConverter<string?, string?> Text(IFieldEncryptor encryptor)
    {
        ArgumentNullException.ThrowIfNull(encryptor);

        return new ValueConverter<string?, string?>(
            plain => string.IsNullOrWhiteSpace(plain) ? null : encryptor.Encrypt(plain),
            cipher => string.IsNullOrEmpty(cipher) ? null : encryptor.Decrypt(cipher));
    }

    /// <summary>
    /// Encrypted date, carried as <c>yyyy-MM-dd</c>.
    ///
    /// <para>
    /// The format string and <see cref="CultureInfo.InvariantCulture"/> are both explicit, and that
    /// is the whole point: a round-trip through the process culture would write <c>02/04/1987</c>
    /// on a fr-FR host and read it back as 4 February on an invariant one. Encrypted, the damage is
    /// silent — the value decrypts fine and is simply the wrong day. The repository has already
    /// paid for this once, in the spreadsheet readers.
    /// </para>
    /// </summary>
    public static ValueConverter<DateOnly?, string?> Date(IFieldEncryptor encryptor)
    {
        ArgumentNullException.ThrowIfNull(encryptor);

        return new ValueConverter<DateOnly?, string?>(
            date => date.HasValue
                ? encryptor.Encrypt(date.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture))
                : null,
            cipher => ParseDate(encryptor.Decrypt(cipher)));
    }

    private static DateOnly? ParseDate(string? plain)
        => DateOnly.TryParseExact(plain, "yyyy-MM-dd", CultureInfo.InvariantCulture,
               DateTimeStyles.None, out var parsed)
            ? parsed
            : null;
}
