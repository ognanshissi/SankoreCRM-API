namespace Sankore.Modules.Administration.Features.ImportUsers.Readers;

using Google.Apis.Auth.OAuth2;
using Google.Apis.Services;
using Google.Apis.Sheets.v4;
using Microsoft.Extensions.Options;

/// <summary>
/// Reads user rows from a Google Sheets spreadsheet.
/// Expects columns: FirstName, LastName, Email, AgencyCode, RoleCode, DefaultLanguage, SpokenLanguages, Specialties.
/// </summary>
public sealed class GoogleSheetsImportReader(IOptions<GoogleImportSettings> settings)
    : IUserImportSourceReader
{
    public async Task<List<ImportUserRow>> ReadAsync(string sourceReference, CancellationToken ct)
    {
        var cfg = settings.Value;
        var credential = GoogleCredential
            .FromFile(cfg.ServiceAccountKeyPath)
            .CreateScoped(SheetsService.Scope.SpreadsheetsReadonly);

        using var service = new SheetsService(new BaseClientService.Initializer
        {
            HttpClientInitializer = credential,
            ApplicationName = cfg.ApplicationName
        });

        // sourceReference = spreadsheetId or full URL — extract ID
        var spreadsheetId = ExtractSpreadsheetId(sourceReference);
        var range = "Sheet1"; // reads entire first sheet

        var request = service.Spreadsheets.Values.Get(spreadsheetId, range);
        var response = await request.ExecuteAsync(ct);
        var values = response.Values;

        if (values is null || values.Count < 2)
            return [];

        // Row 0 = headers
        var headers = values[0].Select((v, i) => (Name: v?.ToString()?.Trim() ?? "", Index: i))
            .ToDictionary(h => h.Name, h => h.Index, StringComparer.OrdinalIgnoreCase);

        int Col(string name) => headers.GetValueOrDefault(name, -1);
        string? Cell(IList<object> row, string name)
        {
            var idx = Col(name);
            return idx >= 0 && idx < row.Count ? row[idx]?.ToString()?.Trim() : null;
        }

        var rows = new List<ImportUserRow>();
        for (int i = 1; i < values.Count; i++)
        {
            var row = values[i];
            var email = Cell(row, "Email");
            if (string.IsNullOrWhiteSpace(email)) continue;

            rows.Add(new ImportUserRow
            {
                FirstName       = Cell(row, "FirstName") ?? "",
                LastName        = Cell(row, "LastName") ?? "",
                Email           = email,
                AgencyCode      = Cell(row, "AgencyCode"),
                RoleCode        = Cell(row, "RoleCode"),
                DefaultLanguage = Cell(row, "DefaultLanguage") ?? "fr",
                SpokenLanguages = Cell(row, "SpokenLanguages"),
                Specialties     = Cell(row, "Specialties"),
            });
        }

        return rows;
    }

    private static string ExtractSpreadsheetId(string input)
    {
        // Handle full URL: https://docs.google.com/spreadsheets/d/{id}/...
        if (input.Contains("/d/"))
        {
            var start = input.IndexOf("/d/") + 3;
            var end = input.IndexOf('/', start);
            return end > start ? input[start..end] : input[start..];
        }
        return input; // already an ID
    }
}
