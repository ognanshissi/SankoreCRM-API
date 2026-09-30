namespace Sankore.Shared.Spreadsheets;

using System.Globalization;
using ClosedXML.Excel;

/// <summary>
/// Reads a spreadsheet cell as the text an import parser expects: always invariant, never the
/// server's culture.
///
/// Every import in this codebase is string-first — Excel and Google Sheets only hand back text,
/// and typed binding would abort a whole file on one malformed cell — then parses those strings
/// with <see cref="CultureInfo.InvariantCulture"/>. ClosedXML's <c>GetString()</c>, however,
/// renders a TYPED cell with the culture of the process. The two disagree, and an operator's
/// spreadsheet stores coordinates, amounts and dates as typed cells, not as text:
///
/// <list type="bullet">
/// <item>a numeric cell on a fr-FR host arrives as "5,300489" and is rejected as "not a number"
///   — for every row at once, since a column is formatted as a whole;</item>
/// <item>a date cell arrives as "02/04/1987", which the invariant short-date pattern
///   (MM/dd/yyyy) happily reads as 4 February instead of 2 April. That one is silent, which
///   makes it worse than the rejection.</item>
/// </list>
///
/// Both failures depend on where the process runs, so they survive every test on a machine
/// whose culture happens to be invariant. Hence one helper, used by all three importers.
/// </summary>
public static class XlsxCell
{
    /// <summary>
    /// Trimmed text of <paramref name="cell"/>: numbers and dates formatted invariantly,
    /// text returned untouched so files that already carry "5.300489" keep working.
    /// </summary>
    public static string ReadAsText(IXLCell cell) => cell.DataType switch
    {
        // "0.###############" rather than "R", which round-trips at the cost of
        // 5.3004889999999996, and rather than the default "G", which switches to scientific
        // notation on large amounts — both shapes the parsers reject.
        XLDataType.Number => cell.GetDouble().ToString("0.###############", CultureInfo.InvariantCulture),

        // ISO 8601, which is what the import row formats document.
        XLDataType.DateTime => cell.GetDateTime().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),

        XLDataType.Boolean => cell.GetBoolean() ? "true" : "false",

        _ => cell.GetString().Trim(),
    };

    /// <summary>Same, but <c>null</c> rather than an empty string — the shape importers want.</summary>
    public static string? ReadAsTextOrNull(IXLCell cell)
        => ReadAsText(cell) is { Length: > 0 } value ? value : null;
}
