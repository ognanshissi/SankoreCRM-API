namespace Sankore.Modules.Customers.Features.Import.Readers;

using System.Globalization;
using ClosedXML.Excel;
using CsvHelper;
using CsvHelper.Configuration;
using Sankore.Shared.Kernel;

/// <summary>Reads client rows from a CSV or Excel (.xlsx) file held by <see cref="IFileStore"/>.</summary>
public sealed class ClientFileImportReader(IFileStore fileStore) : IClientImportSourceReader
{
    public async Task<List<ImportClientRow>> ReadAsync(string sourceReference, CancellationToken ct)
    {
        await using var stream = await fileStore.ReadAsync(sourceReference, ct);

        return sourceReference.EndsWith(".xlsx", StringComparison.OrdinalIgnoreCase)
            ? ReadExcel(stream)
            : ReadCsv(stream);
    }

    private static List<ImportClientRow> ReadCsv(Stream stream)
    {
        using var reader = new StreamReader(stream);
        using var csv = new CsvReader(reader, new CsvConfiguration(CultureInfo.InvariantCulture)
        {
            // A column the template does not know about is ignored rather than fatal: an
            // operator's working file usually has notes columns of their own.
            HeaderValidated = null,
            MissingFieldFound = null,
            TrimOptions = TrimOptions.Trim,
        });

        return csv.GetRecords<ImportClientRow>().ToList();
    }

    private static List<ImportClientRow> ReadExcel(Stream stream)
    {
        using var workbook = new XLWorkbook(stream);
        var ws = workbook.Worksheets.First();

        var headerRow = ws.Row(1);
        var headers = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var lastColumn = ws.LastColumnUsed()?.ColumnNumber() ?? 0;
        for (var col = 1; col <= lastColumn; col++)
            headers[headerRow.Cell(col).GetString().Trim()] = col;

        var rows = new List<ImportClientRow>();
        var lastRow = ws.LastRowUsed()?.RowNumber() ?? 1;

        for (var r = 2; r <= lastRow; r++)
        {
            var row = ws.Row(r);

            string? Cell(string name) =>
                headers.TryGetValue(name, out var col)
                    ? row.Cell(col).GetString().Trim() is { Length: > 0 } v ? v : null
                    : null;

            var candidate = new ImportClientRow
            {
                FirstName = Cell("FirstName"),
                LastName = Cell("LastName"),
                Gender = Cell("Gender"),
                DateOfBirth = Cell("DateOfBirth"),
                Nationality = Cell("Nationality"),
                IdentityDocumentType = Cell("IdentityDocumentType"),
                IdentityDocumentNumber = Cell("IdentityDocumentNumber"),
                Profession = Cell("Profession"),
                LegalName = Cell("LegalName"),
                LegalFormCode = Cell("LegalFormCode"),
                RegistrationNumber = Cell("RegistrationNumber"),
                TaxIdNumber = Cell("TaxIdNumber"),
                IncorporationDate = Cell("IncorporationDate"),
                PhoneNumber = Cell("PhoneNumber"),
                Email = Cell("Email"),
                AgencyCode = Cell("AgencyCode"),
                PreferredLanguage = Cell("PreferredLanguage"),
                AddressStreet = Cell("AddressStreet"),
                AddressCity = Cell("AddressCity"),
                AddressCountry = Cell("AddressCountry"),
            };

            // Skip the blank rows spreadsheets accumulate below real data; anything with a name
            // is kept so the validator — not the reader — decides what is wrong with it.
            if (candidate.LastName is null && candidate.LegalName is null && candidate.PhoneNumber is null)
                continue;

            rows.Add(candidate);
        }

        return rows;
    }
}
