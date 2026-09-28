namespace Sankore.Shared.Infrastructure.Crypto;

using System.Globalization;
using System.Text;

/// <summary>
/// Produces the partially hidden rendering of a sensitive value that every
/// read endpoint returns by default (the clear value is only served by the
/// explicit, rate-limited and audited "reveal" endpoint).
///
/// The mask is deliberately NOT length-preserving for documents and emails: a
/// fixed number of bullets avoids leaking how long the hidden part is.
/// Every method returns <c>null</c> for a <c>null</c> (or blank) input.
/// </summary>
public static class SensitiveValueMasker
{
    /// <summary>The single mask character used everywhere.</summary>
    public const char MaskChar = '•'; // •

    /// <summary>
    /// First 2 and last 2 characters kept, 7 bullets in between:
    /// <c>"CI12345642"</c> → <c>"CI•••••••42"</c>.
    /// A value of 4 characters or fewer is fully masked.
    /// </summary>
    public static string? MaskDocument(string? clear)
    {
        if (string.IsNullOrWhiteSpace(clear)) return null;

        var value = clear.Trim();
        if (value.Length <= 4) return Bullets(4);

        return string.Concat(value[..2], Bullets(7), value[^2..]);
    }

    /// <summary>
    /// Keeps a leading '+', the country calling code, the first 2 and the last 2
    /// digits of the national number; everything in between becomes bullet pairs:
    /// <c>"+22507080918"</c> → <c>"+225 07 •• •• 18"</c>.
    /// A value with fewer than 5 digits is fully masked.
    /// </summary>
    public static string? MaskPhone(string? clear)
    {
        if (string.IsNullOrWhiteSpace(clear)) return null;

        var trimmed = clear.Trim();
        var hasPlus = trimmed.StartsWith('+');

        var digits = new string([.. trimmed.Where(char.IsAsciiDigit)]);
        if (digits.Length < 5) return Bullets(4);

        // A '+' or a long value means the indicatif is present (3 digits here —
        // every West-African calling code is 3 digits long).
        string? country = null;
        var national = digits;
        if ((hasPlus || digits.Length >= 11) && digits.Length >= 8)
        {
            country = digits[..3];
            national = digits[3..];
        }

        var head = national[..2];
        var tail = national[^2..];
        var middle = national[2..^2];

        var parts = new List<string>(5);
        if (country is not null) parts.Add((hasPlus ? "+" : string.Empty) + country);
        else if (hasPlus) parts.Add("+");
        parts.Add(head);
        for (var i = 0; i < middle.Length; i += 2)
            parts.Add(Bullets(Math.Min(2, middle.Length - i)));
        parts.Add(tail);

        return string.Join(' ', parts);
    }

    /// <summary>
    /// Keeps the first character of the local part, the first character of the
    /// domain and the top-level domain: <c>"alice@gmail.com"</c> →
    /// <c>"a•••@g•••.com"</c>. Falls back to <see cref="MaskGeneric"/> when the
    /// value is not shaped like an email address.
    /// </summary>
    public static string? MaskEmail(string? clear)
    {
        if (string.IsNullOrWhiteSpace(clear)) return null;

        var value = clear.Trim();
        var at = value.LastIndexOf('@');
        if (at <= 0 || at == value.Length - 1) return MaskGeneric(value);

        var local = value[..at];
        var domain = value[(at + 1)..];
        var dot = domain.LastIndexOf('.');
        if (dot <= 0 || dot == domain.Length - 1) return MaskGeneric(value);

        var maskedLocal = local[..1] + Bullets(3);
        var maskedDomain = domain[..1] + Bullets(3) + domain[dot..];

        return $"{maskedLocal}@{maskedDomain}";
    }

    /// <summary>
    /// Keeps the first and last character with 3 bullets in between
    /// (<c>"Abidjan"</c> → <c>"A•••n"</c>). A value of 4 characters or fewer is
    /// fully masked.
    /// </summary>
    public static string? MaskGeneric(string? clear)
    {
        if (string.IsNullOrWhiteSpace(clear)) return null;

        var value = clear.Trim();
        if (value.Length <= 4) return Bullets(4);

        return string.Concat(value[..1], Bullets(3), value[^1..]);
    }

    /// <summary>Keeps the year only: <c>1987-04-02</c> → <c>"••/••/1987"</c>.</summary>
    public static string? MaskDate(DateOnly? d)
    {
        if (d is null) return null;

        return string.Concat(
            Bullets(2), "/", Bullets(2), "/",
            d.Value.Year.ToString("D4", CultureInfo.InvariantCulture));
    }

    private static string Bullets(int count) => new(MaskChar, count);
}
