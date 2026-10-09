namespace Sankore.Modules.Integration.Features.Mappings.Csv;

using System.Globalization;
using System.Text;
using CsvHelper;
using CsvHelper.Configuration;
using Sankore.Shared.Kernel;

/// <summary>
/// Reads the uploaded mapping file from the <see cref="IFileStore"/>.
///
/// <para>
/// CSV only. The other three importers of the repo also take .xlsx, but they reference
/// <c>Sankore.Shared.Spreadsheets</c> for it; this module does not, and pulling ClosedXML in for
/// a three-column correspondence table would ship a workbook engine to every deployment that
/// integrates a CBS. Should .xlsx be wanted here, the rule is unchanged: every cell goes through
/// <c>XlsxCell.ReadAsText</c>, never <c>cell.GetString()</c>, which renders a typed cell with the
/// PROCESS culture and silently turns 02/04/1987 into 4 February.
/// </para>
/// </summary>
internal sealed class MappingImportReader(IFileStore fileStore)
{
    internal const string Extension = ".csv";

    internal async Task<MappingImportFile> ReadAsync(string fileReference, CancellationToken ct)
    {
        await using var stream = await fileStore.ReadAsync(fileReference, ct);

        // detectEncodingFromByteOrderMarks: our own export writes a BOM (Excel needs it), so the
        // most likely file to be re-imported is one we wrote.
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        using var csv = new CsvReader(reader, new CsvConfiguration(CultureInfo.InvariantCulture)
        {
            // Same three dials as ClientFileImportReader: an operator's working file usually
            // carries notes columns of its own, and a line missing a field is a line to report,
            // not an exception to abort on.
            HeaderValidated = null,
            MissingFieldFound = null,
            TrimOptions = TrimOptions.Trim,
        });

        csv.Context.RegisterClassMap<MappingImportRowMap>();

        if (!await csv.ReadAsync())
            return MappingImportFile.Invalid(
                $"The file is empty. A header line is required: {MappingCsvColumns.Header}.");

        csv.ReadHeader();

        var headers = csv.HeaderRecord ?? [];
        var missing = MappingCsvColumns.Required
            .Where(c => !headers.Contains(c, StringComparer.OrdinalIgnoreCase))
            .ToArray();

        if (missing.Length > 0)
            return MappingImportFile.Invalid(
                $"The header line is missing the column(s) {string.Join(", ", missing)}. "
                + $"Expected: {MappingCsvColumns.Header}.");

        var rows = new List<MappingImportRow>();

        while (await csv.ReadAsync())
        {
            var row = csv.GetRecord<MappingImportRow>();

            // The parser's physical line, so the report points at the operator's own file.
            row.RowNumber = csv.Parser.Row;

            // Blank becomes null, in ONE place. With MissingFieldFound suppressed, CsvHelper
            // hands back "" for a column the line never had — so a short line and a line with an
            // empty cell arrive differently while meaning the same thing to an operator. The
            // repo's spreadsheet reader draws the same line (XlsxCell.ReadAsTextOrNull), and
            // without it an empty label reaches the database as "" instead of null.
            row.Normalize();

            if (row.IsBlank) continue;

            rows.Add(row);
        }

        return MappingImportFile.Valid(rows);
    }
}

/// <summary>
/// A read file: either a header problem — which fails the whole upload, there being nothing to
/// report per line — or the lines, whatever is wrong with them.
/// </summary>
internal sealed record MappingImportFile(string? HeaderError, IReadOnlyList<MappingImportRow> Rows)
{
    internal bool HasHeaderError => HeaderError is not null;

    internal static MappingImportFile Invalid(string error) => new(error, []);

    internal static MappingImportFile Valid(IReadOnlyList<MappingImportRow> rows) => new(null, rows);
}
