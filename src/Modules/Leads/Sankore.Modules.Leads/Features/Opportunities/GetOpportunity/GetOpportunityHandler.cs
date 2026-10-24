namespace Sankore.Modules.Leads.Features.Opportunities.GetOpportunity;

using MediatR;
using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Leads.Infrastructure;
using Sankore.Modules.Leads.Features.Visibility;
using Sankore.Shared.Kernel;

internal sealed class GetOpportunityHandler(
    LeadsDbContext db,
    LeadVisibility? visibility = null,
    ProjectTemplates.ProjectTemplateResolver? templates = null)
    : IRequestHandler<GetOpportunityQuery, Result<OpportunityDto>>
{
    public async Task<Result<OpportunityDto>> Handle(
        GetOpportunityQuery query, CancellationToken ct)
    {
        var opportunities = visibility?.Apply(db.Opportunities, db.Leads) ?? db.Opportunities;
        var opp = await opportunities
            .Where(o => o.Id == query.OpportunityId)
            .Select(o => new OpportunityDto(
                o.Id, o.LeadId, o.CustomerEntityType, o.CustomerEntityId,
                o.Title, o.Description, o.Product, o.EstimatedAmount,
                o.Stage, o.Probability, o.ExpectedCloseDate, o.OwnerId,
                o.CreatedAt, o.UpdatedAt, o.ClosedAt, o.CloseReason,
                o.NextStep, o.NextStepDueOn, o.SeparateNeedConfirmedAt, o.StepId))
            .FirstOrDefaultAsync(ct);

        if (opp is null)
            return Result.Fail<OpportunityDto>("OPPORTUNITY_NOT_FOUND");

        return Result.Ok(templates is null ? opp : (await new[] { opp }.WithStepsAsync(templates, ct))[0]);
    }
}
