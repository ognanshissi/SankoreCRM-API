namespace Sankore.Modules.Customers.Features.Import.Readers;

using System.Text.RegularExpressions;
using Google.Apis.Auth.OAuth2;
using Google.Apis.Services;
using Google.Apis.Sheets.v4;
using Microsoft.Extensions.Options;
using Sankore.Shared.Infrastructure.Google;

/// <summary>
/// Reads client rows from a Google Sheets spreadsheet, same columns as the CSV template.
/// </summary>
public sealed partial class ClientGoogleSheetsImportReader(IOptions<GoogleImportSettings> settings)
    : IClientImportSourceReader
{
    public async Task<List<ImportClientRow>> ReadAsync(string sourceReference, CancellationToken ct)
    {
        var cfg = settings.Value;

        var credential = GoogleCredential
            .FromFile(cfg.ServiceAccountKeyPath)
            .CreateScoped(SheetsService.Scope.SpreadsheetsReadonly);

        using var service = new SheetsService(new BaseClientService.Initializer
        {
            HttpClientInitializer = credential,
            ApplicationName = cfg.ApplicationName,
        });

        var response = await service.Spreadsheets.Values
            .Get(ExtractSpreadsheetId(sourceReference), "Sheet1")
            .ExecuteAsync(ct);

        var values = response.Values;
        if (values is null || values.Count < 2)
            return [];

        var headers = values[0]
            .Select((v, i) => (Name: v?.ToString()?.Trim() ?? string.Empty, Index: i))
            .Where(h => h.Name.Length > 0)
            .ToDictionary(h => h.Name, h => h.Index, StringComparer.OrdinalIgnoreCase);

        var rows = new List<ImportClientRow>(values.Count - 1);

        for (var r = 1; r < values.Count; r++)
        {
            var line = values[r];

            string? Cell(string name)
            {
                if (!headers.TryGetValue(name, out var i) || i >= line.Count) return null;
                var value = line[i]?.ToString()?.Trim();
                return string.IsNullOrEmpty(value) ? null : value;
            }

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

            if (candidate.LastName is null && candidate.LegalName is null && candidate.PhoneNumber is null)
                continue;

            rows.Add(candidate);
        }

        return rows;
    }

    /// <summary>Accepts either a bare spreadsheet id or the URL an operator copied from the browser.</summary>
    internal static string ExtractSpreadsheetId(string reference)
    {
        var match = SpreadsheetUrl().Match(reference);
        return match.Success ? match.Groups[1].Value : reference.Trim();
    }

    [GeneratedRegex(@"/spreadsheets/d/([a-zA-Z0-9-_]+)")]
    private static partial Regex SpreadsheetUrl();
}
