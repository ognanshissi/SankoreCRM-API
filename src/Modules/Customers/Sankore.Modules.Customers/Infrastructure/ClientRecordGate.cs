using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Administration.PublicApi;
using Sankore.Modules.Administration.PublicApi.Forms;
using Sankore.Modules.Customers.Domain;
using Sankore.Shared.Infrastructure.Workflow;

namespace Sankore.Modules.Customers.Infrastructure;

/// <summary>
/// A client record's answers to a workflow step waiting on it. Presence only: an encrypted field is
/// filled when its ciphertext is there — nothing is decrypted, the Workflow module never sees a value.
/// A client has no questionnaire of its own (they belong to projects), and is its own KYC subject.
/// </summary>
internal sealed class ClientRecordGate(CustomersDbContext db, IAdministrationModule administration) : IWorkflowRecordGate
{
    public string EntityType => "Account";

    private static readonly Dictionary<string, Func<Client, bool>> Natives = new(StringComparer.OrdinalIgnoreCase)
    {
        ["firstName"]                 = c => !string.IsNullOrWhiteSpace(c.FirstName),
        ["lastName"]                  = c => !string.IsNullOrWhiteSpace(c.LastName),
        ["maidenName"]                = c => !string.IsNullOrWhiteSpace(c.MaidenName),
        ["gender"]                    = c => c.Gender.HasValue,
        ["dateOfBirth"]               = c => !string.IsNullOrWhiteSpace(c.EncryptedDateOfBirth),
        ["birthPlace"]                = c => !string.IsNullOrWhiteSpace(c.BirthPlace),
        ["nationality"]               = c => !string.IsNullOrWhiteSpace(c.Nationality),
        ["maritalStatus"]             = c => c.MaritalStatus.HasValue,
        ["fatherName"]                = c => !string.IsNullOrWhiteSpace(c.FatherName),
        ["motherName"]                = c => !string.IsNullOrWhiteSpace(c.MotherName),
        ["profession"]                = c => !string.IsNullOrWhiteSpace(c.Profession),
        ["employer"]                  = c => !string.IsNullOrWhiteSpace(c.Employer),
        ["declaredIncome"]            = c => !string.IsNullOrWhiteSpace(c.EncryptedDeclaredIncome),
        ["preferredLanguage"]         = c => !string.IsNullOrWhiteSpace(c.PreferredLanguage),
        ["identityDocumentType"]      = c => c.IdentityDocumentType.HasValue,
        ["identityDocumentNumber"]    = c => !string.IsNullOrWhiteSpace(c.EncryptedIdentityDocumentNumber),
        ["identityDocumentIssuedOn"]  = c => c.IdentityDocumentIssuedOn.HasValue,
        ["identityDocumentExpiresOn"] = c => c.IdentityDocumentExpiresOn.HasValue,
        ["legalName"]                 = c => !string.IsNullOrWhiteSpace(c.LegalName),
        ["legalFormCode"]             = c => !string.IsNullOrWhiteSpace(c.LegalFormCode),
        ["registrationNumber"]        = c => !string.IsNullOrWhiteSpace(c.EncryptedRegistrationNumber),
        ["taxIdNumber"]               = c => !string.IsNullOrWhiteSpace(c.EncryptedTaxIdNumber),
        ["incorporationDate"]         = c => c.IncorporationDate.HasValue,
        ["advisorUserId"]             = c => c.AdvisorUserId.HasValue,
    };

    /// <summary>Read off the client's active contact points rather than a column.</summary>
    private static readonly Dictionary<string, ContactPointType> ContactKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        ["email"] = ContactPointType.Email,
        ["phoneNumbers"] = ContactPointType.Phone,
    };

    public async Task<IReadOnlyList<RecordFormField>> FieldsAsync(Guid tenantId, CancellationToken ct)
    {
        var form = await administration.GetEntityFormAsync(tenantId, FormEntity.Client, ct);
        return form.Fields
            .Where(f => !f.IsHidden && !f.IsArchived
                     && (!f.IsNative || Natives.ContainsKey(f.Key) || ContactKeys.ContainsKey(f.Key)))
            .OrderBy(f => f.Order)
            .Select(f => new RecordFormField(f.Key, f.Label))
            .ToList();
    }

    public async Task<IReadOnlyList<string>> MissingFieldsAsync(Guid tenantId, Guid entityId, IReadOnlyCollection<string> keys, CancellationToken ct)
    {
        var client = await db.Clients.IgnoreQueryFilters().FirstOrDefaultAsync(c => c.TenantId == tenantId && c.Id == entityId, ct);
        if (client is null) return [];

        var contacts = await db.ClientContactPoints.IgnoreQueryFilters()
            .Where(cp => cp.TenantId == tenantId && cp.ClientId == entityId && cp.ValidTo == null)
            .Select(cp => cp.Type)
            .Distinct()
            .ToListAsync(ct);
        var custom = CustomValues(client.CustomFieldsJson);

        var fields = await FieldsAsync(tenantId, ct);
        return fields
            .Where(f => keys.Contains(f.Key, StringComparer.OrdinalIgnoreCase))
            .Where(f => Natives.TryGetValue(f.Key, out var filled) ? !filled(client)
                      : ContactKeys.TryGetValue(f.Key, out var type) ? !contacts.Contains(type)
                      : !custom.Contains(f.Key))
            .Select(f => f.Label)
            .ToList();
    }

    public Task<int?> PendingQuestionnairesAsync(Guid tenantId, Guid entityId, CancellationToken ct)
        => Task.FromResult<int?>(null);

    public Task<Guid?> ClientIdAsync(Guid tenantId, Guid entityId, CancellationToken ct)
        => Task.FromResult<Guid?>(entityId);

    private static HashSet<string> CustomValues(string? json)
    {
        var filled = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(json)) return filled;
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return filled;
            foreach (var p in doc.RootElement.EnumerateObject())
            {
                var empty = p.Value.ValueKind switch
                {
                    JsonValueKind.Null or JsonValueKind.Undefined => true,
                    JsonValueKind.String => string.IsNullOrWhiteSpace(p.Value.GetString()),
                    JsonValueKind.Array => p.Value.GetArrayLength() == 0,
                    _ => false,
                };
                if (!empty) filled.Add(p.Name);
            }
        }
        catch (JsonException)
        {
        }
        return filled;
    }
}
