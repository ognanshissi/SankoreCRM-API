namespace Sankore.Modules.Customers.Domain.Matching;

using System.Text;

/// <summary>
/// Deterministic, dependency-free Double Metaphone encoder — primary code only, 4 characters.
/// <para>
/// This is a readable subset of Lawrence Philips' algorithm: every branch that mattered for
/// English/Germanic/Slavic disambiguation has been kept only when it changes the output for
/// names we actually store. Three deliberate deviations from the reference implementation,
/// all motivated by French / West-African orthography:
/// </para>
/// <list type="bullet">
///   <item><c>W</c> before a vowel encodes to <c>W</c> (reference: <c>A</c>) — Wattara, Wade and
///         Wone would otherwise collapse onto every vowel-initial name.</item>
///   <item>an initial <c>Y</c> before a vowel encodes to <c>Y</c> (reference: <c>A</c>) — Yao, Yeo.</item>
///   <item><c>TH</c> encodes to <c>T</c> (reference: <c>0</c>, the theta) — there is no /θ/ here.</item>
/// </list>
/// <para>
/// Feed it <see cref="WestAfricanNameNormalizer.Normalize(string)"/> output: this encoder assumes
/// upper-case ASCII letters and does no folding of its own.
/// </para>
/// </summary>
internal static class DoubleMetaphone
{
    private const int MaxLength = 4;

    /// <summary>Returns the 4-character primary code of <paramref name="word"/>, or "" when empty.</summary>
    public static string Encode(string word)
    {
        if (string.IsNullOrWhiteSpace(word))
            return string.Empty;

        var w = KeepLetters(word.ToUpperInvariant());
        if (w.Length == 0)
            return string.Empty;

        var code = new StringBuilder(MaxLength);
        var last = w.Length - 1;
        var i = 0;

        // The first letter of these digraphs is silent: GNome, KNee, PNeumatic, WRight, PSalm.
        if (At(w, 0, "GN", "KN", "PN", "WR", "PS"))
            i = 1;

        // Initial X is pronounced /s/: Xavier.
        if (w[0] == 'X')
        {
            Append(code, 'S');
            i = 1;
        }

        while (i <= last && code.Length < MaxLength)
        {
            switch (w[i])
            {
                // ── Vowels: only a leading vowel is encoded at all ───────────
                case 'A':
                case 'E':
                case 'I':
                case 'O':
                case 'U':
                    if (i == 0) Append(code, 'A');
                    i++;
                    break;

                case 'Y':
                    // Initial Y + vowel is the consonant /j/ (Yao); elsewhere Y behaves as a vowel.
                    if (i == 0) Append(code, IsVowel(CharAt(w, 1)) ? 'Y' : 'A');
                    i++;
                    break;

                case 'B':
                    Append(code, 'P');
                    i += At(w, i, "BB") ? 2 : 1;
                    break;

                case 'C':
                    i = EncodeC(w, i, last, code);
                    break;

                case 'D':
                    if (At(w, i, "DG"))
                    {
                        // DGE / DGI / DGY are /dʒ/ (Badger); any other DG is /dk/.
                        if (CharAt(w, i + 2) is 'E' or 'I' or 'Y')
                        {
                            Append(code, 'J');
                            i += 3;
                        }
                        else
                        {
                            Append(code, 'T');
                            Append(code, 'K');
                            i += 2;
                        }
                    }
                    else
                    {
                        Append(code, 'T');
                        i += At(w, i, "DD", "DT") ? 2 : 1;
                    }

                    break;

                case 'F':
                    Append(code, 'F');
                    i += At(w, i, "FF") ? 2 : 1;
                    break;

                case 'G':
                    i = EncodeG(w, i, code);
                    break;

                case 'H':
                    // H is only audible at the start of a word before a vowel, or between two vowels.
                    if ((i == 0 || IsVowel(CharAt(w, i - 1))) && IsVowel(CharAt(w, i + 1)))
                        Append(code, 'H');
                    i++;
                    break;

                case 'J':
                    Append(code, 'J');
                    i += At(w, i, "JJ") ? 2 : 1;
                    break;

                case 'K':
                    Append(code, 'K');
                    i += At(w, i, "KK") ? 2 : 1;
                    break;

                case 'L':
                    Append(code, 'L');
                    i += At(w, i, "LL") ? 2 : 1;
                    break;

                case 'M':
                    Append(code, 'M');
                    // Silent B in a final MB ("thumb"); a medial MB keeps both sounds ("Bamba").
                    i += At(w, i, "MM") || (At(w, i, "MB") && i + 1 == last) ? 2 : 1;
                    break;

                case 'N':
                    Append(code, 'N');
                    i += At(w, i, "NN") ? 2 : 1;
                    break;

                case 'P':
                    if (At(w, i, "PH"))
                    {
                        Append(code, 'F');
                        i += 2;
                    }
                    else
                    {
                        Append(code, 'P');
                        i += At(w, i, "PP", "PB") ? 2 : 1;
                    }

                    break;

                case 'Q':
                    Append(code, 'K');
                    i += At(w, i, "QQ") ? 2 : 1;
                    break;

                case 'R':
                    Append(code, 'R');
                    i += At(w, i, "RR") ? 2 : 1;
                    break;

                case 'S':
                    i = EncodeS(w, i, code);
                    break;

                case 'T':
                    i = EncodeT(w, i, code);
                    break;

                case 'V':
                    Append(code, 'F');
                    i += At(w, i, "VV") ? 2 : 1;
                    break;

                case 'W':
                    if (At(w, i, "WH"))
                    {
                        Append(code, 'W');
                        i += 2;
                    }
                    else
                    {
                        // Deviation: /w/ is a real consonant here (Wattara, Kwasi).
                        if (IsVowel(CharAt(w, i + 1)))
                            Append(code, 'W');
                        i++;
                    }

                    break;

                case 'X':
                    Append(code, 'K');
                    Append(code, 'S');
                    i += At(w, i, "XX", "XC") ? 2 : 1;
                    break;

                case 'Z':
                    // ZH is /ʒ/ (Zhirinovsky); any other Z is /z/, coded S.
                    if (At(w, i, "ZH"))
                    {
                        Append(code, 'J');
                        i += 2;
                    }
                    else
                    {
                        Append(code, 'S');
                        i += At(w, i, "ZZ") ? 2 : 1;
                    }

                    break;

                default:
                    i++;
                    break;
            }
        }

        return code.ToString();
    }

    // ── C ───────────────────────────────────────────────────────────────────
    private static int EncodeC(string w, int i, int last, StringBuilder code)
    {
        if (At(w, i, "CH"))
        {
            // CH before a consonant is the Greek /k/ (CHRis, CHLoe, teCHNo);
            // before a vowel or at the end of the word it is /ʃ~tʃ/ (CHeikh, marCH).
            var after = CharAt(w, i + 2);
            Append(code, after != '\0' && !IsVowel(after) ? 'K' : 'X');
            return i + 2;
        }

        if (At(w, i, "CIA"))
        {
            Append(code, 'X');
            return i + 3;
        }

        if (At(w, i, "CK", "CQ", "CG", "CC"))
        {
            Append(code, 'K');
            return i + 2;
        }

        if (At(w, i, "CE", "CI", "CY"))
        {
            Append(code, 'S');
            return i + 2;
        }

        Append(code, 'K');
        _ = last;
        return i + 1;
    }

    // ── G ───────────────────────────────────────────────────────────────────
    private static int EncodeG(string w, int i, StringBuilder code)
    {
        if (At(w, i, "GH"))
        {
            // GH after a vowel is silent (ThouGHt); elsewhere it is /g/ (GHana).
            if (i > 0 && IsVowel(CharAt(w, i - 1)))
                return i + 2;

            Append(code, 'K');
            return i + 2;
        }

        if (At(w, i, "GN"))
        {
            Append(code, 'N');
            return i + 2;
        }

        if (At(w, i, "GE", "GI", "GY"))
        {
            Append(code, 'J');
            return i + 2;
        }

        Append(code, 'K');
        return i + (At(w, i, "GG") ? 2 : 1);
    }

    // ── S ───────────────────────────────────────────────────────────────────
    private static int EncodeS(string w, int i, StringBuilder code)
    {
        if (At(w, i, "SCH"))
        {
            // Germanic SCH is /ʃ/ (SCHmidt). SCHool-style /sk/ is rare in this corpus.
            Append(code, 'X');
            return i + 3;
        }

        if (At(w, i, "SH"))
        {
            Append(code, 'X');
            return i + 2;
        }

        if (At(w, i, "SIO", "SIA"))
        {
            Append(code, 'X');
            return i + 3;
        }

        Append(code, 'S');
        return i + (At(w, i, "SS") ? 2 : 1);
    }

    // ── T ───────────────────────────────────────────────────────────────────
    private static int EncodeT(string w, int i, StringBuilder code)
    {
        if (At(w, i, "TIO", "TIA"))
        {
            Append(code, 'X');
            return i + 3;
        }

        if (At(w, i, "TCH"))
        {
            Append(code, 'X');
            return i + 3;
        }

        if (At(w, i, "TH"))
        {
            // Deviation: /t/, not the reference theta.
            Append(code, 'T');
            return i + 2;
        }

        Append(code, 'T');
        return i + (At(w, i, "TT", "TD") ? 2 : 1);
    }

    // ── Helpers ─────────────────────────────────────────────────────────────
    private static string KeepLetters(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (var ch in s)
        {
            if (ch is >= 'A' and <= 'Z')
                sb.Append(ch);
        }

        return sb.ToString();
    }

    private static void Append(StringBuilder code, char c)
    {
        if (code.Length < MaxLength)
            code.Append(c);
    }

    private static char CharAt(string s, int index) => index >= 0 && index < s.Length ? s[index] : '\0';

    private static bool IsVowel(char c) => c is 'A' or 'E' or 'I' or 'O' or 'U' or 'Y';

    private static bool At(string s, int start, params string[] candidates)
    {
        if (start < 0)
            return false;

        foreach (var candidate in candidates)
        {
            if (start + candidate.Length <= s.Length
                && string.CompareOrdinal(s, start, candidate, 0, candidate.Length) == 0)
            {
                return true;
            }
        }

        return false;
    }
}
