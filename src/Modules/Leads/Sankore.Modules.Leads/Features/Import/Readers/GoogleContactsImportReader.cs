namespace Sankore.Modules.Leads.Features.Import.Readers;

using Google.Apis.Auth.OAuth2;
using Google.Apis.PeopleService.v1;
using Google.Apis.Services;
using Microsoft.Extensions.Options;
using Sankore.Shared.Infrastructure.Google;

/// <summary>
/// Reads contacts from the Google People API and maps them to lead rows.
/// A lead needs a phone number, so contacts without one are skipped. Contacts
/// carry no product, language or source — those come from the import defaults.
/// </summary>
public sealed class GoogleContactsImportReader(IOptions<GoogleImportSettings> settings)
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
            .CreateScoped(PeopleServiceService.Scope.ContactsReadonly);

        using var service = new PeopleServiceService(new BaseClientService.Initializer
        {
            HttpClientInitializer = credential,
            ApplicationName       = cfg.ApplicationName
        });

        var rows = new List<ImportLeadRow>();
        string? pageToken = null;

        do
        {
            var request = service.People.Connections.List("people/me");
            request.PersonFields = "names,emailAddresses,phoneNumbers,organizations";
            request.PageSize     = 100;
            request.PageToken    = pageToken;

            var response = await request.ExecuteAsync(ct);

            foreach (var person in response.Connections ?? [])
            {
                var phone = person.PhoneNumbers?.FirstOrDefault()?.Value?.Trim();
                if (string.IsNullOrWhiteSpace(phone)) continue;

                var name = person.Names?.FirstOrDefault();

                rows.Add(new ImportLeadRow
                {
                    FullName    = name?.DisplayName?.Trim(),
                    FirstName   = name?.GivenName?.Trim(),
                    LastName    = name?.FamilyName?.Trim(),
                    PhoneNumber = phone,
                    Email       = person.EmailAddresses?.FirstOrDefault()?.Value?.Trim(),
                    CompanyName = person.Organizations?.FirstOrDefault()?.Name?.Trim(),
                    ExternalReference = person.ResourceName,
                });
            }

            pageToken = response.NextPageToken;
        } while (pageToken is not null);

        return rows;
    }
}
