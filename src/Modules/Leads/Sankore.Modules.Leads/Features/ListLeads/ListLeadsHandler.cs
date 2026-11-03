namespace Sankore.Modules.Leads.Features.ListLeads;

using MediatR;
using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Leads.Domain;
using Sankore.Modules.Leads.Features.GetLead;
using Sankore.Modules.Leads.Features.NextAction;
using Sankore.Modules.Leads.Infrastructure;
using Sankore.Modules.Leads.Features.Visibility;
using Sankore.Shared.Kernel;

internal sealed class ListLeadsHandler(
    LeadsDbContext db,
    Sankore.Modules.Administration.PublicApi.IAdministrationModule? administration = null,
    ITenantContext? tenant = null,
    ILeadSettings? settings = null,
    LeadVisibility? visibility = null)
    : IRequestHandler<ListLeadsQuery, Result<PagedResult<LeadDto>>>
{
    public async Task<Result<PagedResult<LeadDto>>> Handle(
        ListLeadsQuery query, CancellationToken ct)
    {
        // Assigned-only for an agent; a test or a caller with lead:read:all keeps the whole tenant.
        var q = visibility?.Apply(db.Leads.AsNoTracking()) ?? db.Leads.AsNoTracking();

        if (query.Ids is { Count: > 0 } ids)
        {
            if (ids.Count > ListLeadsQuery.MaxIds)
                return Result.Fail<PagedResult<LeadDto>>($"{ListLeadsQuery.MaxIds} identifiants au plus par appel.");
            q = q.Where(l => ids.Contains(l.Id));
        }

        var filtered = await query.Filter.ApplyAsync(q, db, administration, tenant, ct);
        if (filtered.IsFailure)
            return Result.Fail<PagedResult<LeadDto>>(filtered.Error!);
        q = filtered.Value;

        var total = await q.CountAsync(ct);

        var page   = query.Page < 1 ? 1 : query.Page;
        var size   = query.PageSize < 1 ? 20 : query.PageSize > 100 ? 100 : query.PageSize;

        var items = await q
            .OrderByDescending(l => l.CreatedAt)
            .Skip((page - 1) * size)
            .Take(size)
            .Select(lead => new LeadDto(
                lead.Id,
                lead.TenantId,
                lead.FullName,
                lead.FirstName,
                lead.LastName,
                lead.PhoneNumber,
                lead.Email,
                lead.Gender.ToString(),
                lead.DateOfBirth,
                lead.CompanyName,
                lead.CompanyEmail,
                lead.CompanyPhone,
                lead.Website,
                lead.Status.ToString(),
                lead.PipelineStage.ToString(),
                lead.Source.ToString(),
                lead.Channel == null ? null : lead.Channel.ToString(),
                lead.Campaign,
                lead.ExternalReference,
                lead.Comment,
                lead.CapturedAt,
                lead.CreatedAt,
                lead.UpdatedAt,
                lead.ExpiresAt,
                lead.InterestedProduct,
                lead.DesiredAmount,
                lead.PreferredLanguage,
                lead.QualificationCompleteness,
                lead.Score,
                lead.IntentLevel.ToString(),
                lead.Location == null ? (double?)null : lead.Location.Latitude,
                lead.Location == null ? (double?)null : lead.Location.Longitude,
                lead.PreferredAgencyId,
                lead.OwnerId,
                lead.AgencyId,
                lead.CurrentAssignedId,
                lead.CurrentAssignmentId,
                lead.LastActivityAt,
                lead.LossReason,
                lead.ConvertedAt,
                lead.ConvertedToCustomerId,
                lead.NationalId,
                lead.CustomerReference,
                lead.ProspectType.ToString(),
                // Appended last on purpose — this construction is fully positional, and the
                // record's neighbours are same-typed, so an inserted argument would bind
                // silently to the wrong member.
                lead.LeadSourceConfigId,
                null,
                null,
                lead.AwaitingDispatchSince))
            .ToListAsync(ct);

        // Code and label come from a second read rather than a join in the projection above: a
        // correlated subquery would run once per row, and the tenant's source table is small
        // enough to hand back as a lookup. An id that resolves to nothing stays null — the
        // reference is opaque, so a source may have been archived since the lead arrived.
        var sourceIds = items
            .Where(i => i.LeadSourceConfigId.HasValue)
            .Select(i => i.LeadSourceConfigId!.Value)
            .Distinct()
            .ToList();

        if (sourceIds.Count > 0)
        {
            var sources = await db.LeadSourceConfigs
                .AsNoTracking()
                .Where(s => sourceIds.Contains(s.Id))
                .Select(s => new { s.Id, s.Code, s.Label })
                .ToListAsync(ct);

            var byId = sources.ToDictionary(s => s.Id);

            for (var i = 0; i < items.Count; i++)
            {
                if (items[i].LeadSourceConfigId is { } id && byId.TryGetValue(id, out var src))
                    items[i] = items[i] with { LeadSourceCode = src.Code, LeadSourceLabel = src.Label };
            }
        }

        // The next step is what a list of leads is scanned for: resolved for the page in three
        // queries, by the same resolver as the lead screen so the two never disagree.
        var facts = items
            .Select(i => new LeadFacts(
                i.Id, Enum.Parse<LeadStatus>(i.Status), i.Score, i.QualificationCompleteness,
                i.LastActivityAt, i.CurrentAssignmentId, i.CurrentAssignedId, i.OwnerId))
            .ToList();
        var nextSteps = await LeadNextSteps.ForLeadsAsync(db, facts, DateTimeOffset.UtcNow, ct);
        for (var i = 0; i < items.Count; i++)
        {
            if (nextSteps.TryGetValue(items[i].Id, out var step))
                items[i] = items[i] with { NextStep = step };
        }

        var assignmentIds = items
            .Where(i => i.CurrentAssignmentId.HasValue)
            .Select(i => i.CurrentAssignmentId!.Value)
            .ToList();
        if (assignmentIds.Count > 0)
        {
            var slas = await LeadSlas.ForAssignmentsAsync(db, settings, items[0].TenantId, assignmentIds, ct);
            for (var i = 0; i < items.Count; i++)
            {
                if (items[i].CurrentAssignmentId is { } aid && slas.TryGetValue(aid, out var sla))
                    items[i] = items[i] with { Sla = sla };
            }
        }

        return Result.Ok(new PagedResult<LeadDto>(items, total, page, size));
    }
}
