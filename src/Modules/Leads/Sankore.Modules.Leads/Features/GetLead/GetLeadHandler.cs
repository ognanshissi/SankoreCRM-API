namespace Sankore.Modules.Leads.Features.GetLead;

using MediatR;
using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Leads.Infrastructure;
using Sankore.Shared.Kernel;

internal sealed class GetLeadHandler(LeadsDbContext db)
    : IRequestHandler<GetLeadQuery, Result<LeadDto>>
{
    public async Task<Result<LeadDto>> Handle(GetLeadQuery query, CancellationToken ct)
    {
        var lead = await db.Leads
            .AsNoTracking()
            .FirstOrDefaultAsync(l => l.Id == query.LeadId, ct);

        if (lead is null)
            return Result.Fail<LeadDto>("LEAD_NOT_FOUND");

        // Resolved for display. Stays null when the lead carries no configured source, and also
        // when the id no longer resolves: the reference is opaque with no foreign key, so the
        // source may have been archived since the lead arrived.
        var source = lead.LeadSourceConfigId is { } sourceId
            ? await db.LeadSourceConfigs
                .AsNoTracking()
                .Where(s => s.Id == sourceId)
                .Select(s => new { s.Code, s.Label })
                .FirstOrDefaultAsync(ct)
            : null;

        return Result.Ok(new LeadDto(
            Id:                       lead.Id,
            TenantId:                 lead.TenantId,
            FullName:                 lead.FullName,
            FirstName:                lead.FirstName,
            LastName:                 lead.LastName,
            PhoneNumber:              lead.PhoneNumber,
            Email:                    lead.Email,
            Gender:                   lead.Gender.ToString(),
            DateOfBirth:              lead.DateOfBirth,
            CompanyName:              lead.CompanyName,
            CompanyEmail:             lead.CompanyEmail,
            CompanyPhone:             lead.CompanyPhone,
            Website:                  lead.Website,
            Status:                   lead.Status.ToString(),
            PipelineStage:            lead.PipelineStage.ToString(),
            Source:                   lead.Source.ToString(),
            Channel:                  lead.Channel?.ToString(),
            Campaign:                 lead.Campaign,
            ExternalReference:        lead.ExternalReference,
            Comment:                  lead.Comment,
            CapturedAt:               lead.CapturedAt,
            CreatedAt:                lead.CreatedAt,
            UpdatedAt:                lead.UpdatedAt,
            ExpiresAt:                lead.ExpiresAt,
            InterestedProduct:        lead.InterestedProduct,
            DesiredAmount:            lead.DesiredAmount,
            PreferredLanguage:        lead.PreferredLanguage,
            QualificationCompleteness: lead.QualificationCompleteness,
            Score:                    lead.Score,
            IntentLevel:              lead.IntentLevel.ToString(),
            Latitude:                 lead.Location?.Latitude,
            Longitude:                lead.Location?.Longitude,
            PreferredAgencyId:        lead.PreferredAgencyId,
            OwnerId:                  lead.OwnerId,
            AgencyId:                 lead.AgencyId,
            CurrentAssignedId:        lead.CurrentAssignedId,
            CurrentAssignmentId:      lead.CurrentAssignmentId,
            LastActivityAt:           lead.LastActivityAt,
            LossReason:               lead.LossReason,
            ConvertedAt:              lead.ConvertedAt,
            ConvertedToCustomerId:    lead.ConvertedToCustomerId,
            NationalId:               lead.NationalId,
            CustomerReference:        lead.CustomerReference,
            ProspectType:             lead.ProspectType.ToString(),
            LeadSourceConfigId:       lead.LeadSourceConfigId,
            LeadSourceCode:           source?.Code,
            LeadSourceLabel:          source?.Label));
    }
}
