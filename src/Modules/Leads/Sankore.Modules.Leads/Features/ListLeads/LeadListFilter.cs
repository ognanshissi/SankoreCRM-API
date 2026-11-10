namespace Sankore.Modules.Leads.Features.ListLeads;

using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Administration.PublicApi;
using Sankore.Modules.Administration.PublicApi.Forms;
using Sankore.Modules.Leads.Domain;
using Sankore.Modules.Leads.Infrastructure;
using Sankore.Shared.Kernel;

/// <summary>
/// The filters of the lead list, applied the same way by the list, its export and its view counts.
/// Each carried its own copy before, and the export fell behind the list's views (2026-11-03).
/// </summary>
internal sealed record LeadListFilter(
    LeadStatus? Status = null,
    PipelineStage? PipelineStage = null,
    LeadSource? Source = null,
    Guid? OwnerId = null,
    Guid? AgencyId = null,
    string? Search = null,
    string? Tag = null,
    LeadIntentLevel? IntentLevel = null,
    Guid? LeadSourceConfigId = null,
    bool? AwaitingDispatch = null,
    /// <summary>Custom-field filters, <c>key:value</c> — see <c>CustomFieldValues.FilterDocuments</c>.</summary>
    IReadOnlyList<string>? CustomFilters = null,
    /// <summary>Assigned to this agent, or theirs as owner while nobody is assigned — the « Agent » column.</summary>
    Guid? AgentId = null,
    /// <summary>Person or company (key accounts lot 1).</summary>
    LeadType? ProspectType = null,
    /// <summary>A sector code of <c>LeadActivitySectors</c>.</summary>
    string? ActivitySector = null,
    LeadCompanySize? CompanySize = null,
    /// <summary>Against the institution's key-account thresholds (key accounts lot 4).</summary>
    LeadPotentialTier? Potential = null)
{
    public async Task<Result<IQueryable<Lead>>> ApplyAsync(
        IQueryable<Lead> q, LeadsDbContext db, IAdministrationModule? administration, ITenantContext? tenant, CancellationToken ct)
    {
        if (Status.HasValue)
            q = q.Where(l => l.Status == Status.Value);

        if (PipelineStage.HasValue)
            q = q.Where(l => l.PipelineStage == PipelineStage.Value);

        if (Source.HasValue)
            q = q.Where(l => l.Source == Source.Value);

        // Narrows to one configured source, which the coarse Source enum cannot express.
        if (LeadSourceConfigId.HasValue)
            q = q.Where(l => l.LeadSourceConfigId == LeadSourceConfigId.Value);

        if (OwnerId.HasValue)
            q = q.Where(l => l.OwnerId == OwnerId.Value);

        if (AgentId is { } agent)
            q = q.Where(l => l.CurrentAssignedId == agent || (l.CurrentAssignedId == null && l.OwnerId == agent));

        if (AgencyId.HasValue)
            q = q.Where(l => l.AgencyId == AgencyId.Value);

        if (!string.IsNullOrWhiteSpace(Search))
        {
            var search = Search.Trim().ToLower();
            q = q.Where(l =>
                l.FullName.ToLower().Contains(search) ||
                l.PhoneNumber.Contains(search));
        }

        // The dispatch queue: leads no agent could take yet, still unassigned. Assignment clears
        // the mark, the second predicate only guards a row caught mid-write.
        if (AwaitingDispatch == true)
            q = q.Where(l => l.AwaitingDispatchSince != null && l.CurrentAssignmentId == null);

        if (IntentLevel.HasValue)
            q = q.Where(l => l.IntentLevel == IntentLevel.Value);

        if (ProspectType.HasValue)
            q = q.Where(l => l.ProspectType == ProspectType.Value);

        // An unknown sector is a 400, never an ignored filter — as a custom field's.
        if (!string.IsNullOrWhiteSpace(ActivitySector))
        {
            var sector = LeadActivitySectors.Normalize(ActivitySector);
            if (sector is null)
                return Result.Fail<IQueryable<Lead>>("LEAD_ACTIVITY_SECTOR_INVALID");
            q = q.Where(l => l.ActivitySectorCode == sector);
        }

        if (CompanySize.HasValue)
            q = q.Where(l => l.CompanySize == CompanySize.Value);

        if (Potential is { } tier)
        {
            if (tenant is null)
                return Result.Fail<IQueryable<Lead>>("KEY_ACCOUNTS_UNAVAILABLE");
            var settings = await Settings.KeyAccounts.KeyAccountSettings.ReadAsync(db, tenant.CurrentTenantId, ct);
            q = settings.WhereTier(q, tier);
        }

        // Custom-field filters (dynamic forms, lot 6): a jsonb containment per filter, served by the
        // GIN index on custom_fields. The form decides each value's shape — see FilterDocuments.
        if (CustomFilters is { Count: > 0 })
        {
            if (administration is null || tenant is null)
                return Result.Fail<IQueryable<Lead>>("CUSTOM_FILTERS_UNAVAILABLE");

            var form = await administration.GetEntityFormAsync(tenant.CurrentTenantId, FormEntity.Lead, ct);
            var documents = CustomFieldValues.FilterDocuments(form, CustomFilters);
            if (documents.IsFailure)
                return Result.Fail<IQueryable<Lead>>(documents.Error!);

            foreach (var document in documents.Value)
                q = q.Where(l => l.CustomFieldsJson != null && EF.Functions.JsonContains(l.CustomFieldsJson, document));
        }

        if (!string.IsNullOrWhiteSpace(Tag))
        {
            var tag = Tag.Trim().ToLowerInvariant();
            q = q.Where(l => db.LeadTags.Any(t => t.LeadId == l.Id && t.Tag == tag));
        }

        return Result.Ok(q);
    }
}
