namespace Sankore.Modules.Leads.Features.ListLeads;

using MediatR;
using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Leads.Domain;
using Sankore.Modules.Leads.Features.GetLead;
using Sankore.Modules.Leads.Infrastructure;
using Sankore.Shared.Kernel;

internal sealed class ListLeadsHandler(LeadsDbContext db)
    : IRequestHandler<ListLeadsQuery, Result<PagedResult<LeadDto>>>
{
    public async Task<Result<PagedResult<LeadDto>>> Handle(
        ListLeadsQuery query, CancellationToken ct)
    {
        var q = db.Leads.AsNoTracking();

        if (query.Status.HasValue)
            q = q.Where(l => l.Status == query.Status.Value);

        if (query.PipelineStage.HasValue)
            q = q.Where(l => l.PipelineStage == query.PipelineStage.Value);

        if (query.Source.HasValue)
            q = q.Where(l => l.Source == query.Source.Value);

        if (query.OwnerId.HasValue)
            q = q.Where(l => l.OwnerId == query.OwnerId.Value);

        if (query.AgencyId.HasValue)
            q = q.Where(l => l.AgencyId == query.AgencyId.Value);

        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            var search = query.Search.Trim().ToLower();
            q = q.Where(l =>
                l.FullName.ToLower().Contains(search) ||
                l.PhoneNumber.Contains(search));
        }

        if (!string.IsNullOrWhiteSpace(query.Tag))
        {
            var tag = query.Tag.Trim().ToLowerInvariant();
            q = q.Where(l => db.LeadTags.Any(t => t.LeadId == l.Id && t.Tag == tag));
        }

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
                lead.ConvertedToCustomerId))
            .ToListAsync(ct);

        return Result.Ok(new PagedResult<LeadDto>(items, total, page, size));
    }
}
