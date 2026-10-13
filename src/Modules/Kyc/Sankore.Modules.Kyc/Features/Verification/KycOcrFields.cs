namespace Sankore.Modules.Kyc.Features.Verification;

using System.Globalization;

/// <summary>
/// Reading the biometric service's field dictionary — the field names, and nothing about what they
/// mean.
///
/// <para>
/// <b>Why it is shared rather than private to the handler that writes the evidence.</b> The
/// verification writes <c>KycIdentityDocument.OcrFieldsJson</c>, and KRM-B-08's routing then reads
/// it back to compare the name and date of birth against the client record. Two copies of
/// <see cref="Simplify"/> would mean two opinions on whether <c>"given_names"</c> and
/// <c>"givenNames"</c> are the same field — and the routing one would be the copy nobody looks at,
/// so a service rename would silently make every remote file fail its identity check and route to
/// a human. A failure in the safe direction is still a failure nobody would diagnose.
/// </para>
///
/// <para>
/// Field names are the SERVICE's own and it is free to change them — "number" on one deployment,
/// "document_number" on the next. Everything here therefore matches on letters and digits only.
/// </para>
/// </summary>
internal static class KycOcrFields
{
    /// <summary>Spellings of the document number, simplified.</summary>
    private static readonly HashSet<string> DocumentNumberKeys =
    [
        "documentnumber", "documentno", "idnumber", "number", "no", "cardnumber",
    ];

    /// <summary>The surname, in the spellings the service has used.</summary>
    internal static readonly string[] SurnameKeys =
        ["surname", "last_name", "family_name", "nom"];

    /// <summary>The given names, which may arrive as one field or as a first-name field.</summary>
    internal static readonly string[] GivenNamesKeys =
        ["given_names", "first_name", "given_name", "forenames", "prenoms"];

    internal static readonly string[] BirthDateKeys =
        ["date_of_birth", "birth_date", "dob", "date_naissance"];

    internal static readonly string[] ExpiryDateKeys =
        ["expiry_date", "date_of_expiry", "expires_on"];

    internal static bool IsDocumentNumberKey(string key) => DocumentNumberKeys.Contains(Simplify(key));

    internal static string Simplify(string key)
        => new(key.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());

    internal static string? FindDocumentNumber(IReadOnlyDictionary<string, string> fields)
        => fields.FirstOrDefault(f => IsDocumentNumberKey(f.Key) && !string.IsNullOrWhiteSpace(f.Value)).Value;

    internal static Dictionary<string, string> WithoutDocumentNumber(IReadOnlyDictionary<string, string> fields)
        => fields.Where(f => !IsDocumentNumberKey(f.Key)).ToDictionary(f => f.Key, f => f.Value);

    /// <summary>Same rule, for the confidences — one generic would hide which rule is shared.</summary>
    internal static Dictionary<string, double> WithoutDocumentNumber(IReadOnlyDictionary<string, double> values)
        => values.Where(v => !IsDocumentNumberKey(v.Key)).ToDictionary(v => v.Key, v => v.Value);

    /// <summary>The first non-blank value among <paramref name="candidates"/>, or null.</summary>
    internal static string? FindFirst(
        IReadOnlyDictionary<string, string> fields, params string[] candidates)
    {
        foreach (var candidate in candidates)
        {
            var simplified = Simplify(candidate);
            var hit = fields.FirstOrDefault(f => Simplify(f.Key) == simplified).Value;
            if (!string.IsNullOrWhiteSpace(hit)) return hit;
        }

        return null;
    }

    /// <summary>
    /// Day-first formats are tried before the invariant parser, which reads "02/04/1987" as the
    /// 4th of February. An unparsable date is dropped rather than guessed: a wrong expiry silently
    /// downgrades a valid file to the simplified tier later on, and a wrong date of birth fails an
    /// identity comparison that should have passed.
    /// </summary>
    internal static DateOnly? ParseDate(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;

        return DateOnly.TryParseExact(
            raw.Trim(),
            ["yyyy-MM-dd", "dd/MM/yyyy", "d/M/yyyy", "dd-MM-yyyy", "dd.MM.yyyy", "yyyyMMdd"],
            CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed)
            ? parsed
            : null;
    }
}
