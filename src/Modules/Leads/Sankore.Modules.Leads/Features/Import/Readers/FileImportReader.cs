namespace Sankore.Modules.Leads.Features.Import.Readers;

using System.Globalization;
using Sankore.Shared.Kernel;
using ClosedXML.Excel;
using CsvHelper;
using CsvHelper.Configuration;
using Sankore.Shared.Spreadsheets;

/// <summary>
/// Reads lead rows from a CSV or Excel (.xlsx) file held in <see cref="IFileStore"/>.
/// Both formats share the same header names; unknown columns are ignored and
/// missing ones come back null for <see cref="LeadRowParser"/> to resolve.
/// </summary>
public sealed class FileImportReader(IFileStore fileStore) : ILeadImportSourceReader
{
    public async Task<List<ImportLeadRow>> ReadAsync(string sourceReference, CancellationToken ct)
    {
        await using var stream = await fileStore.ReadAsync(sourceReference, ct);

        return sourceReference.EndsWith(".xlsx", StringComparison.OrdinalIgnoreCase)
            ? ReadExcel(stream)
            : ReadCsv(stream);
    }

    private static List<ImportLeadRow> ReadCsv(Stream stream)
    {
        using var reader = new StreamReader(stream);
        using var csv = new CsvReader(reader, new CsvConfiguration(CultureInfo.InvariantCulture)
        {
            HeaderValidated   = null,
            MissingFieldFound = null,
            TrimOptions       = TrimOptions.Trim,
        });

        return csv.GetRecords<ImportLeadRow>().ToList();
    }

    private static List<ImportLeadRow> ReadExcel(Stream stream)
    {
        using var workbook = new XLWorkbook(stream);
        var ws = workbook.Worksheets.First();

        var lastColumn = ws.LastColumnUsed()?.ColumnNumber() ?? 0;
        var lastRow    = ws.LastRowUsed()?.RowNumber() ?? 0;
        if (lastColumn == 0 || lastRow < 2) return [];

        var headerRow = ws.Row(1);
        var headers = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (var col = 1; col <= lastColumn; col++)
        {
            var name = headerRow.Cell(col).GetString().Trim();
            if (name.Length > 0) headers[name] = col;
        }

        var rows = new List<ImportLeadRow>();
        for (var r = 2; r <= lastRow; r++)
        {
            var row = ws.Row(r);
            if (row.IsEmpty()) continue;

            string? Cell(string name)
            {
                if (!headers.TryGetValue(name, out var col)) return null;
                var value = XlsxCell.ReadAsText(row.Cell(col));
                return value.Length == 0 ? null : value;
            }

            rows.Add(new ImportLeadRow
            {
                FullName          = Cell("FullName"),
                FirstName         = Cell("FirstName"),
                LastName          = Cell("LastName"),
                PhoneNumber       = Cell("PhoneNumber"),
                Email             = Cell("Email"),
                Source            = Cell("Source"),
                InterestedProduct = Cell("InterestedProduct"),
                PreferredLanguage = Cell("PreferredLanguage"),
                Latitude          = Cell("Latitude"),
                Longitude         = Cell("Longitude"),
                Gender            = Cell("Gender"),
                DateOfBirth       = Cell("DateOfBirth"),
                DesiredAmount     = Cell("DesiredAmount"),
                DesiredCurrency   = Cell("DesiredCurrency"),
                Campaign          = Cell("Campaign"),
                Channel           = Cell("Channel"),
                Comment           = Cell("Comment"),
                ExternalReference = Cell("ExternalReference"),
                CompanyName       = Cell("CompanyName"),
                OwnerId           = Cell("OwnerId"),
                AgencyId          = Cell("AgencyId"),
            });
        }

        return rows;
    }
}
