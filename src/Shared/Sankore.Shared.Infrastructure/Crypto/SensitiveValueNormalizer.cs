namespace Sankore.Shared.Infrastructure.Crypto;

using System.Globalization;
using System.Text;

/// <summary>
/// Canonicalises a sensitive value BEFORE it is hashed into a blind index.
/// Two values that a human would consider identical must normalize to the very
/// same string, otherwise duplicate detection and exact-match lookups silently
/// miss: spacing, punctuation, accents and casing always vanish. Phone numbers
/// keep their indicatif — see <see cref="NormalizePhone"/> for the exact rule.
/// </summary>
public static class SensitiveValueNormalizer
{
    /// <summary>
    /// A country calling code and whether its national numbering plan uses a
    /// trunk prefix "0" that must be dropped to reach the national significant
    /// number. This distinction is NOT cosmetic:
    /// <list type="bullet">
    /// <item>France has a trunk 0, so <c>+33 0 7 99 88 77 66</c> and
    ///       <c>+33 7 99 88 77 66</c> are the SAME subscriber;</item>
    /// <item>Côte d'Ivoire has had no trunk prefix since the 2021 migration to
    ///       10-digit numbers — the leading 0 of <c>0708091801</c> is part of the
    ///       number, so stripping it would corrupt the value and make two
    ///       different subscribers collide.</item>
    /// </list>
    /// Ordered longest code first so the longest prefix wins.
    /// </summary>
    private static readonly (string Code, bool HasTrunkZero)[] CallingCodes =
    [
        ("225", false), // Côte d'Ivoire — 10 digits, leading 0 is significant
        ("226", false), // Burkina Faso
        ("227", false), // Niger
        ("228", false), // Togo
        ("229", false), // Benin
        ("221", false), // Senegal
        ("223", false), // Mali
        ("224", false), // Guinea
        ("222", false), // Mauritania
        ("220", false), // Gambia
        ("245", false), // Guinea-Bissau
        ("238", false), // Cape Verde
        ("231", true),  // Liberia
        ("232", true),  // Sierra Leone
        ("233", true),  // Ghana
        ("234", true),  // Nigeria
        ("235", false), // Chad
        ("236", false), // Central African Republic
        ("237", false), // Cameroon
        ("240", false), // Equatorial Guinea
        ("241", false), // Gabon
        ("242", false), // Congo
        ("243", true),  // DR Congo
        ("49", true),   // Germany (diaspora)
        ("44", true),   // United Kingdom (diaspora)
        ("41", true),   // Switzerland (diaspora)
        ("39", false),  // Italy — leading 0 is significant
        ("34", false),  // Spain
        ("33", true),   // France (diaspora)
        ("32", true),   // Belgium (diaspora)
        ("31", true),   // Netherlands (diaspora)
        ("1",  false)   // North America
    ];

    /// <summary>
    /// Digits only. The <i>indicatif</i> is KEPT — a number is stored and matched
    /// as it was dialled — and the only thing neutralised is the national trunk
    /// prefix, for the countries whose numbering plan actually has one.
    ///
    /// Rules, in order:
    /// <list type="number">
    /// <item>keep digits only (spaces, dots, dashes, parentheses, '+' dropped);</item>
    /// <item>a leading "00" is the international access prefix and is removed, and
    ///       the value is then treated as international;</item>
    /// <item>when the value is international (raw started with '+', or the digits
    ///       started with "00"), the known calling code is identified and, for a
    ///       country that uses a trunk prefix, a single "0" immediately after the
    ///       calling code is removed. The calling code itself stays.</item>
    /// <item>a value with no '+' / "00" marker is left exactly as dialled — no
    ///       calling code is guessed, no trunk 0 is removed.</item>
    /// </list>
    ///
    /// Worked examples:
    /// <code>
    /// "+33 0 7 99 88 77 66" → "33799887766"   // France: trunk 0 dropped
    /// "+33 7 99 88 77 66"   → "33799887766"   // same subscriber, same index
    /// "0033799887766"       → "33799887766"
    /// "+225 07 08 09 18 01" → "2250708091801" // Côte d'Ivoire: 0 is significant
    /// "07 08 09 18 01"      → "0708091801"    // local form, left as dialled
    /// </code>
    ///
    /// Trade-off, deliberate: a number captured in local form and searched in
    /// international form (or the reverse) will NOT match, because the indicatif is
    /// no longer inferred. This method is the single place to change should
    /// cross-form matching be wanted later.
    /// </summary>
    public static string NormalizePhone(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return string.Empty;

        var isInternational = raw.TrimStart().StartsWith('+');

        var digits = KeepDigits(raw);
        if (digits.Length == 0) return string.Empty;

        if (digits.StartsWith("00", StringComparison.Ordinal))
        {
            digits = digits[2..];
            isInternational = true;
        }

        if (!isInternational) return digits;

        foreach (var (code, hasTrunkZero) in CallingCodes)
        {
            if (!digits.StartsWith(code, StringComparison.Ordinal)) continue;

            var national = digits[code.Length..];

            // Only a plan with a trunk prefix loses its leading 0, and only when a
            // plausible subscriber number remains underneath it.
            if (hasTrunkZero && national.Length > 1 && national[0] == '0')
                national = national[1..];

            return code + national;
        }

        return digits;
    }

    /// <summary>Trimmed and lower-cased (invariant culture).</summary>
    public static string NormalizeEmail(string raw)
        => string.IsNullOrWhiteSpace(raw) ? string.Empty : raw.Trim().ToLowerInvariant();

    /// <summary>Alphanumeric characters only, upper-cased, accents removed.</summary>
    public static string NormalizeDocumentNumber(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return string.Empty;

        var stripped = RemoveDiacritics(raw);
        var sb = new StringBuilder(stripped.Length);
        foreach (var c in stripped)
        {
            if (char.IsLetterOrDigit(c))
                sb.Append(char.ToUpperInvariant(c));
        }

        return sb.ToString();
    }

    /// <summary>ISO date, e.g. "1987-04-02".</summary>
    public static string NormalizeDateOfBirth(DateOnly d)
        => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    /// <summary>Upper-cased, accents removed, punctuation dropped, whitespace collapsed.</summary>
    public static string NormalizeAddress(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return string.Empty;

        var stripped = RemoveDiacritics(raw);
        var sb = new StringBuilder(stripped.Length);
        var pendingSpace = false;

        foreach (var c in stripped)
        {
            if (char.IsLetterOrDigit(c))
            {
                if (pendingSpace && sb.Length > 0) sb.Append(' ');
                pendingSpace = false;
                sb.Append(char.ToUpperInvariant(c));
            }
            else
            {
                // Whitespace AND punctuation both act as a single separator.
                pendingSpace = true;
            }
        }

        return sb.ToString();
    }

    /// <summary>Decomposes then drops every combining mark: "Côté" → "Cote".</summary>
    public static string RemoveDiacritics(string raw)
    {
        if (string.IsNullOrEmpty(raw)) return string.Empty;

        var decomposed = raw.Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(decomposed.Length);

        foreach (var c in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)
                sb.Append(c);
        }

        return sb.ToString().Normalize(NormalizationForm.FormC);
    }

    private static string KeepDigits(string raw)
    {
        var sb = new StringBuilder(raw.Length);
        foreach (var c in raw)
        {
            if (char.IsAsciiDigit(c)) sb.Append(c);
        }

        return sb.ToString();
    }
}
