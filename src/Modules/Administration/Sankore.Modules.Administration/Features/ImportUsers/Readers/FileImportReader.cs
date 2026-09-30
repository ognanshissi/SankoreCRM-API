using Sankore.Shared.Kernel;

namespace Sankore.Modules.Administration.Features.ImportUsers.Readers;

using System.Globalization;
using ClosedXML.Excel;
using Sankore.Shared.Spreadsheets;
using CsvHelper;
using CsvHelper.Configuration;

/// <summary>
/// Reads user rows from a CSV or Excel (.xlsx) file stored via IFileStore.
/// </summary>
public sealed class FileImportReader(IFileStore fileStore) : IUserImportSourceReader
{
    public async Task<List<ImportUserRow>> ReadAsync(string sourceReference, CancellationToken ct)
    {
        await using var stream = await fileStore.ReadAsync(sourceReference, ct);

        // Determine format from the stored file reference (extension hint)
        if (sourceReference.EndsWith(".xlsx", StringComparison.OrdinalIgnoreCase))
            return ReadExcel(stream);

        return await ReadCsv(stream, ct);
    }

    private static async Task<List<ImportUserRow>> ReadCsv(Stream stream, CancellationToken ct)
    {
        using var reader = new StreamReader(stream);
        using var csv = new CsvReader(reader, new CsvConfiguration(CultureInfo.InvariantCulture)
        {
            HeaderValidated = null,
            MissingFieldFound = null,
            TrimOptions = TrimOptions.Trim,
        });

        return csv.GetRecords<ImportUserRow>().ToList();
    }

    private static List<ImportUserRow> ReadExcel(Stream stream)
    {
        using var workbook = new XLWorkbook(stream);
        var ws = workbook.Worksheets.First();
        var rows = new List<ImportUserRow>();

        // Expect header row 1: FirstName, LastName, Email, AgencyCode, RoleCode, DefaultLanguage, SpokenLanguages, Specialties
        var headerRow = ws.Row(1);
        var headers = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (int col = 1; col <= ws.LastColumnUsed()?.ColumnNumber(); col++)
            headers[XlsxCell.ReadAsText(headerRow.Cell(col))] = col;

        int Col(string name) => headers.GetValueOrDefault(name, 0);

        for (int r = 2; r <= ws.LastRowUsed()?.RowNumber(); r++)
        {
            var row = ws.Row(r);
            // XlsxCell keeps every column culture-independent. These are all text today, but an
            // AgencyCode typed as a number is one spreadsheet away, and the failure would be
            // silent on the operator's machine and loud only on the server.
            string Text(string name) => Col(name) > 0 ? XlsxCell.ReadAsText(row.Cell(Col(name))) : "";
            string? TextOrNull(string name) => Col(name) > 0 ? XlsxCell.ReadAsTextOrNull(row.Cell(Col(name))) : null;

            var email = Text("Email");
            if (string.IsNullOrWhiteSpace(email)) continue;

            rows.Add(new ImportUserRow
            {
                FirstName       = Text("FirstName"),
                LastName        = Text("LastName"),
                Email           = email,
                AgencyCode      = TextOrNull("AgencyCode"),
                RoleCode        = TextOrNull("RoleCode"),
                // Deliberately NOT "TextOrNull(...) ?? \"fr\"": a present-but-blank cell must stay
                // empty so the validator still reports "DefaultLanguage is required", the way the
                // CSV path does. Only an absent column falls back.
                DefaultLanguage = Col("DefaultLanguage") > 0 ? Text("DefaultLanguage") : "fr",
                SpokenLanguages = TextOrNull("SpokenLanguages"),
                Specialties     = TextOrNull("Specialties"),
            });
        }

        return rows;
    }
}
