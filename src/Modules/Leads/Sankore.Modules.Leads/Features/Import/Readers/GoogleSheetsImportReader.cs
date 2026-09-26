namespace Sankore.Modules.Leads.Features.Import.Readers;

using Google.Apis.Auth.OAuth2;
using Google.Apis.Services;
using Google.Apis.Sheets.v4;
using Microsoft.Extensions.Options;
using Sankore.Shared.Infrastructure.Google;

/// <summary>
/// Reads lead rows from the first sheet of a Google Spreadsheet, using the same
/// header names as the file import. Row 1 is the header row.
/// </summary>
public sealed class GoogleSheetsImportReader(IOptions<GoogleImportSettings> settings)
    : ILeadImportSourceReader
{
    public async Task<List<ImportLeadRow>> ReadAsync(string sourceReference, CancellationToken ct)
    {
        var cfg = settings.Value;
        if (string.IsNullOrWhiteSpace(cfg.ServiceAccountKeyPath))
            throw new InvalidOperationException(
                "Google import is not configured: set GoogleImport:ServiceAccountKeyPath.");

        var credential = GoogleCredential
            .FromFile(cfg.ServiceAccountKeyPath)
            .CreateScoped(SheetsService.Scope.SpreadsheetsReadonly);

        using var service = new SheetsService(new BaseClientService.Initializer
        {
            HttpClientInitializer = credential,
            ApplicationName       = cfg.ApplicationName
        });

        var request  = service.Spreadsheets.Values.Get(ExtractSpreadsheetId(sourceReference), "Sheet1");
        var response = await request.ExecuteAsync(ct);
        var values   = response.Values;

        if (values is null || values.Count < 2) return [];

        var headers = values[0]
            .Select((v, i) => (Name: v?.ToString()?.Trim() ?? "", Index: i))
            .Where(h => h.Name.Length > 0)
            .ToDictionary(h => h.Name, h => h.Index, StringComparer.OrdinalIgnoreCase);

        var rows = new List<ImportLeadRow>();
        for (var i = 1; i < values.Count; i++)
        {
            var row = values[i];

            string? Cell(string name)
            {
                if (!headers.TryGetValue(name, out var idx) || idx >= row.Count) return null;
                var value = row[idx]?.ToString()?.Trim();
                return string.IsNullOrEmpty(value) ? null : value;
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

    private static string ExtractSpreadsheetId(string input)
    {
        // Full URL: https://docs.google.com/spreadsheets/d/{id}/edit#gid=0
        if (!input.Contains("/d/", StringComparison.Ordinal)) return input;

        var start = input.IndexOf("/d/", StringComparison.Ordinal) + 3;
        var end   = input.IndexOf('/', start);
        return end > start ? input[start..end] : input[start..];
    }
}
