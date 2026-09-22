using Sankore.Shared.Kernel;

namespace Sankore.Modules.Administration.Features.ImportUsers.Readers;

using System.Globalization;
using ClosedXML.Excel;
using CsvHelper;
using CsvHelper.Configuration;

/// <summary>
/// Reads user rows from a CSV or Excel (.xlsx) file stored via IImportFileStore.
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
            headers[headerRow.Cell(col).GetString().Trim()] = col;

        int Col(string name) => headers.GetValueOrDefault(name, 0);

        for (int r = 2; r <= ws.LastRowUsed()?.RowNumber(); r++)
        {
            var row = ws.Row(r);
            var email = Col("Email") > 0 ? row.Cell(Col("Email")).GetString().Trim() : "";
            if (string.IsNullOrWhiteSpace(email)) continue;

            rows.Add(new ImportUserRow
            {
                FirstName       = Col("FirstName") > 0 ? row.Cell(Col("FirstName")).GetString().Trim() : "",
                LastName        = Col("LastName") > 0 ? row.Cell(Col("LastName")).GetString().Trim() : "",
                Email           = email,
                AgencyCode      = Col("AgencyCode") > 0 ? row.Cell(Col("AgencyCode")).GetString().Trim() : null,
                RoleCode        = Col("RoleCode") > 0 ? row.Cell(Col("RoleCode")).GetString().Trim() : null,
                DefaultLanguage = Col("DefaultLanguage") > 0 ? row.Cell(Col("DefaultLanguage")).GetString().Trim() : "fr",
                SpokenLanguages = Col("SpokenLanguages") > 0 ? row.Cell(Col("SpokenLanguages")).GetString().Trim() : null,
                Specialties     = Col("Specialties") > 0 ? row.Cell(Col("Specialties")).GetString().Trim() : null,
            });
        }

        return rows;
    }
}
