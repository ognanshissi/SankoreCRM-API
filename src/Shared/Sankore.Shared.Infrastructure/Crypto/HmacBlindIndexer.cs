namespace Sankore.Shared.Infrastructure.Crypto;

using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;

/// <summary>
/// HMAC-SHA256 implementation of <see cref="IBlindIndexer"/>, modelled on the
/// Leads module's <c>HmacPhoneBlindIndexer</c> but purpose-aware.
///
/// Hashed input is <c>purpose + ":" + normalizedValue</c>, which gives domain
/// separation: the index of a phone number can never be matched against the
/// index of a registration number even when the digits are identical.
/// </summary>
public sealed class HmacBlindIndexer : IBlindIndexer
{
    private const int MinimumKeySize = 32;

    private readonly Lazy<byte[]> _key;

    public HmacBlindIndexer(IOptions<FieldProtectionOptions> options)
    {
        _key = new Lazy<byte[]>(() => ReadKey(options.Value.BlindIndexKey, options.Value.SectionName));
    }

    public string Compute(BlindIndexPurpose purpose, string value)
    {
        var normalized = Normalize(purpose, value);
        var input = Encoding.UTF8.GetBytes($"{purpose}:{normalized}");

        using var hmac = new HMACSHA256(_key.Value);
        return Convert.ToHexString(hmac.ComputeHash(input)).ToLowerInvariant();
    }

    private static string Normalize(BlindIndexPurpose purpose, string value) => purpose switch
    {
        BlindIndexPurpose.Phone => SensitiveValueNormalizer.NormalizePhone(value),
        BlindIndexPurpose.Email => SensitiveValueNormalizer.NormalizeEmail(value),
        BlindIndexPurpose.IdentityDocument => SensitiveValueNormalizer.NormalizeDocumentNumber(value),
        BlindIndexPurpose.RegistrationNumber => SensitiveValueNormalizer.NormalizeDocumentNumber(value),
        BlindIndexPurpose.PostalAddress => SensitiveValueNormalizer.NormalizeAddress(value),
        BlindIndexPurpose.DateOfBirth => NormalizeDate(value),
        _ => value.Trim()
    };

    /// <summary>
    /// Dates reach the indexer as strings; any parsable date is canonicalised to
    /// "yyyy-MM-dd" so "02/04/1987" and "1987-04-02" share one index.
    /// </summary>
    private static string NormalizeDate(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;

        var trimmed = value.Trim();

        // Day-first formats are tried BEFORE the invariant parser, which would
        // read "02/04/1987" as the 4th of February (US month-first order).
        if (DateOnly.TryParseExact(
                trimmed,
                ["yyyy-MM-dd", "dd/MM/yyyy", "d/M/yyyy", "dd-MM-yyyy", "dd.MM.yyyy", "yyyyMMdd"],
                CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
            return SensitiveValueNormalizer.NormalizeDateOfBirth(parsed);

        if (DateOnly.TryParse(trimmed, CultureInfo.InvariantCulture,
                DateTimeStyles.None, out parsed))
            return SensitiveValueNormalizer.NormalizeDateOfBirth(parsed);

        return trimmed;
    }

    private static byte[] ReadKey(string? configured, string section)
    {
        const string guidance =
            "Generate one with: Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))";

        if (string.IsNullOrWhiteSpace(configured))
            throw new InvalidOperationException(
                $"{section}:BlindIndexKey is not configured (Base64, ≥ {MinimumKeySize} bytes). {guidance}");

        byte[] key;
        try
        {
            key = Convert.FromBase64String(configured);
        }
        catch (FormatException ex)
        {
            throw new InvalidOperationException(
                $"{section}:BlindIndexKey is not valid Base64. {guidance}", ex);
        }

        if (key.Length < MinimumKeySize)
            throw new InvalidOperationException(
                $"{section}:BlindIndexKey must be at least {MinimumKeySize} bytes once Base64-decoded, "
                + $"but {key.Length} were provided. {guidance}");

        return key;
    }
}
