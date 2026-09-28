namespace Sankore.Modules.Customers.Domain.Matching;

using System.Globalization;
using System.Text;

/// <summary>
/// Builds and normalizes <see cref="Client.SearchKey"/> — the denormalized,
/// accent-free, upper-cased name column that backs prefix search (US-M01-BE-12).
///
/// Names are deliberately NOT encrypted (they are covered by storage-at-rest
/// encryption) precisely so that a prefix search stays an indexable
/// <c>LIKE 'TERM%'</c> instead of a full scan with per-row decryption. This class
/// is the single source of truth for that normalization: the write side (the
/// aggregate) and the read side (the search handler) MUST both go through it,
/// otherwise a client whose name carries an accent becomes unreachable.
///
/// Unlike <see cref="WestAfricanNameNormalizer"/> this performs NO phonetic
/// variant folding — prefix search must preserve the spelling the agent typed.
/// </summary>
public static class SearchKeyBuilder
{
    /// <summary>"Traoré", "Awa" → "TRAORE AWA" (surname first: agents search by surname).</summary>
    public static string ForPerson(string? firstName, string? lastName)
        => NormalizeTerm(string.Join(' ', new[] { lastName, firstName }
            .Where(part => !string.IsNullOrWhiteSpace(part))));

    /// <summary>"Société Générale de Côte d'Ivoire" → "SOCIETE GENERALE DE COTE D IVOIRE".</summary>
    public static string ForLegalEntity(string? legalName) => NormalizeTerm(legalName);

    /// <summary>
    /// Upper-cased, diacritics removed, every run of non-alphanumeric characters
    /// collapsed to a single space, trimmed. Applied identically to the stored key
    /// and to the user's search term.
    /// </summary>
    public static string NormalizeTerm(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return string.Empty;

        var decomposed = raw.Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(decomposed.Length);
        var pendingSpace = false;

        foreach (var c in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark)
                continue;

            if (char.IsLetterOrDigit(c))
            {
                if (pendingSpace && sb.Length > 0) sb.Append(' ');
                pendingSpace = false;
                sb.Append(char.ToUpperInvariant(c));
            }
            else
            {
                pendingSpace = true;
            }
        }

        return sb.ToString().Normalize(NormalizationForm.FormC);
    }
}
