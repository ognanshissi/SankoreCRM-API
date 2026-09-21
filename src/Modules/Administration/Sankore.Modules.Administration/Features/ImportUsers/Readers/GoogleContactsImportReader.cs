namespace Sankore.Modules.Administration.Features.ImportUsers.Readers;

using Google.Apis.Auth.OAuth2;
using Google.Apis.PeopleService.v1;
using Google.Apis.PeopleService.v1.Data;
using Google.Apis.Services;
using Microsoft.Extensions.Options;

/// <summary>
/// Reads contacts from Google People API (Google Contacts) and maps them to import rows.
/// Uses service account credentials. Only contacts with an email address are included.
/// </summary>
public sealed class GoogleContactsImportReader(IOptions<GoogleImportSettings> settings)
    : IUserImportSourceReader
{
    public async Task<List<ImportUserRow>> ReadAsync(string sourceReference, CancellationToken ct)
    {
        var cfg = settings.Value;
        var credential = GoogleCredential
            .FromFile(cfg.ServiceAccountKeyPath)
            .CreateScoped(PeopleServiceService.Scope.ContactsReadonly);

        using var service = new PeopleServiceService(new BaseClientService.Initializer
        {
            HttpClientInitializer = credential,
            ApplicationName = cfg.ApplicationName
        });

        var rows = new List<ImportUserRow>();
        string? pageToken = null;

        do
        {
            var request = service.People.Connections.List("people/me");
            request.PersonFields = "names,emailAddresses,phoneNumbers,organizations";
            request.PageSize = 100;
            request.PageToken = pageToken;

            var response = await request.ExecuteAsync(ct);

            if (response.Connections is not null)
            {
                foreach (var person in response.Connections)
                {
                    var email = person.EmailAddresses?.FirstOrDefault()?.Value;
                    if (string.IsNullOrWhiteSpace(email)) continue;

                    var name = person.Names?.FirstOrDefault();

                    rows.Add(new ImportUserRow
                    {
                        FirstName       = name?.GivenName ?? "",
                        LastName        = name?.FamilyName ?? "",
                        Email           = email.Trim(),
                        DefaultLanguage = "fr",
                    });
                }
            }

            pageToken = response.NextPageToken;
        } while (pageToken is not null);

        return rows;
    }
}
