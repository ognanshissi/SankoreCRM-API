using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Administration.PublicApi;
using Sankore.Modules.Administration.PublicApi.Forms;
using Sankore.Modules.Leads.Domain;
using Sankore.Shared.Infrastructure.Workflow;

namespace Sankore.Modules.Leads.Infrastructure;

/// <summary>
/// The lead's answers to a workflow step waiting on it: which form fields are filled (presence only),
/// how many questionnaires are still to complete, which client it became.
/// </summary>
internal sealed class LeadRecordGate(LeadsDbContext db, IAdministrationModule administration) : IWorkflowRecordGate
{
    public string EntityType => "Lead";

    public Task<IReadOnlyList<RecordFormField>> FieldsAsync(Guid tenantId, CancellationToken ct)
        => LeadFormPresence.FieldsAsync(administration, tenantId, ct);

    public async Task<IReadOnlyList<string>> MissingFieldsAsync(Guid tenantId, Guid entityId, IReadOnlyCollection<string> keys, CancellationToken ct)
    {
        var lead = await db.Leads.IgnoreQueryFilters().FirstOrDefaultAsync(l => l.TenantId == tenantId && l.Id == entityId, ct);
        return lead is null ? [] : await LeadFormPresence.MissingAsync(administration, tenantId, lead, keys, ct);
    }

    public async Task<int?> PendingQuestionnairesAsync(Guid tenantId, Guid entityId, CancellationToken ct)
        => await db.QualificationResponses.IgnoreQueryFilters()
            .CountAsync(r => r.TenantId == tenantId && r.LeadId == entityId && r.Status == QualificationResponseStatus.ToComplete, ct);

    public async Task<Guid?> ClientIdAsync(Guid tenantId, Guid entityId, CancellationToken ct)
        => await db.Leads.IgnoreQueryFilters()
            .Where(l => l.TenantId == tenantId && l.Id == entityId)
            .Select(l => l.ConvertedToCustomerId ?? l.CustomerId)
            .FirstOrDefaultAsync(ct);
}

/// <summary>
/// A project's answers: its form is its lead's (the person being sold to), its questionnaires are its
/// own plus the lead's not tied to a project, its client the one it was opened for — else its lead's.
/// </summary>
internal sealed class ProjectRecordGate(LeadsDbContext db, IAdministrationModule administration) : IWorkflowRecordGate
{
    public string EntityType => "Opportunity";

    public Task<IReadOnlyList<RecordFormField>> FieldsAsync(Guid tenantId, CancellationToken ct)
        => LeadFormPresence.FieldsAsync(administration, tenantId, ct);

    public async Task<IReadOnlyList<string>> MissingFieldsAsync(Guid tenantId, Guid entityId, IReadOnlyCollection<string> keys, CancellationToken ct)
    {
        var leadId = await LeadOfAsync(tenantId, entityId, ct);
        var lead = leadId is null ? null
            : await db.Leads.IgnoreQueryFilters().FirstOrDefaultAsync(l => l.TenantId == tenantId && l.Id == leadId, ct);
        return lead is null ? [] : await LeadFormPresence.MissingAsync(administration, tenantId, lead, keys, ct);
    }

    public async Task<int?> PendingQuestionnairesAsync(Guid tenantId, Guid entityId, CancellationToken ct)
    {
        var leadId = await LeadOfAsync(tenantId, entityId, ct);
        return await db.QualificationResponses.IgnoreQueryFilters()
            .CountAsync(r => r.TenantId == tenantId
                          && r.Status == QualificationResponseStatus.ToComplete
                          && (r.OpportunityId == entityId || (r.OpportunityId == null && leadId != null && r.LeadId == leadId)), ct);
    }

    public async Task<Guid?> ClientIdAsync(Guid tenantId, Guid entityId, CancellationToken ct)
    {
        var project = await db.Opportunities.IgnoreQueryFilters()
            .Where(o => o.TenantId == tenantId && o.Id == entityId)
            .Select(o => new { o.CustomerEntityId, o.LeadId })
            .FirstOrDefaultAsync(ct);
        if (project is null) return null;
        if (project.CustomerEntityId is { } client) return client;
        return project.LeadId is { } leadId
            ? await db.Leads.IgnoreQueryFilters()
                .Where(l => l.TenantId == tenantId && l.Id == leadId)
                .Select(l => l.ConvertedToCustomerId ?? l.CustomerId)
                .FirstOrDefaultAsync(ct)
            : null;
    }

    private Task<Guid?> LeadOfAsync(Guid tenantId, Guid projectId, CancellationToken ct)
        => db.Opportunities.IgnoreQueryFilters()
            .Where(o => o.TenantId == tenantId && o.Id == projectId)
            .Select(o => o.LeadId)
            .FirstOrDefaultAsync(ct);
}

/// <summary>Which lead form fields are filled — presence only, read off the entity.</summary>
internal static class LeadFormPresence
{
    /// <summary>The native fields a step may require, with how to tell they are filled.</summary>
    private static readonly Dictionary<string, Func<Lead, bool>> Natives = new(StringComparer.OrdinalIgnoreCase)
    {
        ["firstName"]         = l => !string.IsNullOrWhiteSpace(l.FirstName),
        ["lastName"]          = l => !string.IsNullOrWhiteSpace(l.LastName),
        ["gender"]            = l => l.Gender != LeadGender.Unknown,
        ["dateOfBirth"]       = l => l.DateOfBirth.HasValue,
        ["nationalId"]        = l => !string.IsNullOrWhiteSpace(l.NationalId),
        ["companyName"]       = l => !string.IsNullOrWhiteSpace(l.CompanyName),
        ["companyEmail"]      = l => !string.IsNullOrWhiteSpace(l.CompanyEmail),
        ["companyPhone"]      = l => !string.IsNullOrWhiteSpace(l.CompanyPhone),
        ["website"]           = l => !string.IsNullOrWhiteSpace(l.Website),
        ["phoneNumber"]       = l => !string.IsNullOrWhiteSpace(l.PhoneNumber),
        ["email"]             = l => !string.IsNullOrWhiteSpace(l.Email),
        ["preferredLanguage"] = l => !string.IsNullOrWhiteSpace(l.PreferredLanguage),
        ["interestedProduct"] = l => !string.IsNullOrWhiteSpace(l.InterestedProduct) && l.InterestedProduct != "Unknown",
        ["desiredAmount"]     = l => l.DesiredAmount is { Amount: > 0 },
        ["comment"]           = l => !string.IsNullOrWhiteSpace(l.Comment),
        ["campaign"]          = l => !string.IsNullOrWhiteSpace(l.Campaign),
        ["externalReference"] = l => !string.IsNullOrWhiteSpace(l.ExternalReference),
        ["customerReference"] = l => !string.IsNullOrWhiteSpace(l.CustomerReference),
        ["preferredAgencyId"] = l => l.PreferredAgencyId.HasValue,
    };

    public static async Task<IReadOnlyList<RecordFormField>> FieldsAsync(IAdministrationModule administration, Guid tenantId, CancellationToken ct)
    {
        var form = await administration.GetEntityFormAsync(tenantId, FormEntity.Lead, ct);
        return form.Fields
            .Where(f => !f.IsHidden && !f.IsArchived && (!f.IsNative || Natives.ContainsKey(f.Key)))
            .OrderBy(f => f.Order)
            .Select(f => new RecordFormField(f.Key, f.Label))
            .ToList();
    }

    public static async Task<IReadOnlyList<string>> MissingAsync(
        IAdministrationModule administration, Guid tenantId, Lead lead, IReadOnlyCollection<string> keys, CancellationToken ct)
    {
        var fields = await FieldsAsync(administration, tenantId, ct);
        var custom = CustomValues(lead.CustomFieldsJson);
        return fields
            .Where(f => keys.Contains(f.Key, StringComparer.OrdinalIgnoreCase))
            .Where(f => Natives.TryGetValue(f.Key, out var filled) ? !filled(lead) : !custom.Contains(f.Key))
            .Select(f => f.Label)
            .ToList();
    }

    /// <summary>The custom keys holding a non-empty value.</summary>
    public static HashSet<string> CustomValues(string? json)
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
