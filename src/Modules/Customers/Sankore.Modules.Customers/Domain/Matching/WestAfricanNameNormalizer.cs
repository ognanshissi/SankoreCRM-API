namespace Sankore.Modules.Customers.Domain.Matching;

using System.Globalization;
using System.Text;

/// <summary>
/// Normalizes a West-African name before it is phonetically encoded.
/// <para>
/// Names in the region are transliterated inconsistently across French, English and
/// local orthographies: the same family name is spelled <c>Ouattara</c> or <c>Wattara</c>,
/// <c>Diallo</c> or <c>Jallo</c>, <c>Kouassi</c> or <c>Kwasi</c>. Double Metaphone alone does
/// not fold those apart because the graphemes differ before they ever reach the encoder.
/// This normalizer therefore applies orthographic variant folding BEFORE metaphone.
/// </para>
/// <para>Rules, applied per word, in this order:</para>
/// <list type="number">
///   <item>uppercase, remove diacritics, drop everything that is not A-Z (words kept separate)</item>
///   <item><c>TCH</c> → <c>CH</c> (Tchibo / Chibo)</item>
///   <item><c>KH</c> → <c>K</c> (Cheikh / Chek), <c>KW</c> stays <c>KW</c></item>
///   <item><c>PH</c> → <c>F</c></item>
///   <item><c>QU</c> → <c>K</c></item>
///   <item><c>DJ</c>, <c>DY</c>, and <c>DI</c> followed by a vowel → <c>J</c> (Diallo / Jallo, Dieng / Jeng)</item>
///   <item><c>OU</c> followed by a vowel → <c>W</c>, anywhere in the word (Ouattara / Wattara, Kouassi / Kwasi)</item>
///   <item><c>NB</c> → <c>MB</c>, <c>NP</c> → <c>MP</c> (nasal assimilation: Banba / Bamba)</item>
///   <item><c>SS</c> → <c>S</c>, then ALL doubled letters collapsed</item>
///   <item>a trailing run of two or more vowels is reduced to its first vowel (Yaou / Yao)</item>
/// </list>
/// </summary>
public static class WestAfricanNameNormalizer
{
    private const string TrailingVowels = "AEIOU";

    /// <summary>Returns the folded, ASCII-only, upper-case form of <paramref name="raw"/>.</summary>
    public static string Normalize(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return string.Empty;

        var result = new StringBuilder(raw.Length);
        foreach (var word in SplitIntoWords(raw))
            result.Append(FoldWord(word));

        return result.ToString();
    }

    /// <summary>Removes combining marks so that <c>Traoré</c> and <c>Traore</c> are the same input.</summary>
    public static string RemoveDiacritics(string raw)
    {
        if (string.IsNullOrEmpty(raw))
            return string.Empty;

        var decomposed = raw.Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(decomposed.Length);
        foreach (var ch in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(ch) != UnicodeCategory.NonSpacingMark)
                sb.Append(ch);
        }

        return sb.ToString().Normalize(NormalizationForm.FormC);
    }

    // ── Step 1: uppercase, de-accent, keep only letters, split on anything else ──
    private static IEnumerable<string> SplitIntoWords(string raw)
    {
        var ascii = RemoveDiacritics(raw.ToUpperInvariant());
        var current = new StringBuilder();

        foreach (var ch in ascii)
        {
            if (ch is >= 'A' and <= 'Z')
            {
                current.Append(ch);
            }
            else if (current.Length > 0)
            {
                yield return current.ToString();
                current.Clear();
            }
        }

        if (current.Length > 0)
            yield return current.ToString();
    }

    // ── Steps 2..10: orthographic variant folding ───────────────────────────
    private static string FoldWord(string word)
    {
        var s = word;

        s = s.Replace("TCH", "CH", StringComparison.Ordinal);
        s = s.Replace("KH", "K", StringComparison.Ordinal);
        s = s.Replace("PH", "F", StringComparison.Ordinal);
        s = s.Replace("QU", "K", StringComparison.Ordinal);
        s = FoldPalatalizedD(s);
        s = FoldOuGlide(s);
        s = s.Replace("NB", "MB", StringComparison.Ordinal);
        s = s.Replace("NP", "MP", StringComparison.Ordinal);
        s = s.Replace("SS", "S", StringComparison.Ordinal);
        s = CollapseDoubledLetters(s);
        s = FoldTrailingVowelRun(s);

        return s;
    }

    /// <summary>DJ / DY / DI+vowel all render the same /dʒ/ onset: fold them to J.</summary>
    private static string FoldPalatalizedD(string s)
    {
        var sb = new StringBuilder(s.Length);
        var i = 0;

        while (i < s.Length)
        {
            if (s[i] == 'D' && i + 1 < s.Length)
            {
                var next = s[i + 1];
                if (next is 'J' or 'Y')
                {
                    sb.Append('J');
                    i += 2;
                    continue;
                }

                // "DI" only palatalizes when a vowel follows (DIALLO -> JALLO, but DIOP keeps its vowel).
                if (next == 'I' && i + 2 < s.Length && IsVowel(s[i + 2]))
                {
                    sb.Append('J');
                    i += 2;
                    continue;
                }
            }

            sb.Append(s[i]);
            i++;
        }

        return sb.ToString();
    }

    /// <summary>OU before a vowel is the glide /w/: OUATTARA -> WATTARA, KOUASSI -> KWASSI.</summary>
    private static string FoldOuGlide(string s)
    {
        var sb = new StringBuilder(s.Length);
        var i = 0;

        while (i < s.Length)
        {
            if (s[i] == 'O' && i + 2 < s.Length && s[i + 1] == 'U' && IsVowel(s[i + 2]))
            {
                sb.Append('W');
                i += 2;
                continue;
            }

            sb.Append(s[i]);
            i++;
        }

        return sb.ToString();
    }

    private static string CollapseDoubledLetters(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (var ch in s)
        {
            if (sb.Length == 0 || sb[^1] != ch)
                sb.Append(ch);
        }

        return sb.ToString();
    }

    /// <summary>YAOU and YAO must agree: keep only the first vowel of a trailing vowel run.</summary>
    private static string FoldTrailingVowelRun(string s)
    {
        if (s.Length < 2)
            return s;

        var start = s.Length;
        while (start > 0 && TrailingVowels.Contains(s[start - 1], StringComparison.Ordinal))
            start--;

        var runLength = s.Length - start;
        return runLength >= 2 ? s[..(start + 1)] : s;
    }

    private static bool IsVowel(char c) => c is 'A' or 'E' or 'I' or 'O' or 'U' or 'Y';
}
