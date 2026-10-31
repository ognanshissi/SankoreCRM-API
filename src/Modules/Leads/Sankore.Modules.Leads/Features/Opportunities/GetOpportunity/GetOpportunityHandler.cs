namespace Sankore.Modules.Leads.Features.Opportunities.GetOpportunity;

using MediatR;
using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Leads.Infrastructure;
using Sankore.Modules.Leads.Features.Visibility;
using Sankore.Shared.Kernel;

internal sealed class GetOpportunityHandler(
    LeadsDbContext db,
    LeadVisibility? visibility = null,
    ProjectTemplates.ProjectTemplateResolver? templates = null,
    ProjectTemplates.ProjectWorkflows? workflows = null,
    Sankore.Modules.Workflow.PublicApi.IWorkflowModule? workflow = null)
    : IRequestHandler<GetOpportunityQuery, Result<OpportunityDto>>
{
    public async Task<Result<OpportunityDto>> Handle(
        GetOpportunityQuery query, CancellationToken ct)
    {
        var opportunities = visibility?.Apply(db.Opportunities, db.Leads) ?? db.Opportunities;
        var row = await opportunities
            .Where(o => o.Id == query.OpportunityId)
            .Select(o => new { o.TenantId, Dto = new OpportunityDto(
                o.Id, o.LeadId, o.CustomerEntityType, o.CustomerEntityId,
                o.Title, o.Description, o.Product, o.EstimatedAmount,
                o.Stage, o.Probability, o.ExpectedCloseDate, o.OwnerId,
                o.CreatedAt, o.UpdatedAt, o.ClosedAt, o.CloseReason,
                o.NextStep, o.NextStepDueOn, o.SeparateNeedConfirmedAt, o.StepId)
            {
                TemplateVersionId = o.TemplateVersionId,
            } })
            .FirstOrDefaultAsync(ct);

        if (row is null)
            return Result.Fail<OpportunityDto>("OPPORTUNITY_NOT_FOUND");

        // The same reading that refuses « Marquer gagné »: without it the agent learnt about a
        // blocking step of a circuit started by an event only when the win was refused.
        var opp = row.Dto;
        if (workflow is not null && opp.ClosedAt is null)
            opp = opp with
            {
                BlockingSteps = await workflow.GetBlockingStepsAsync(row.TenantId, "Opportunity", opp.Id, ct),
            };

        IReadOnlyList<OpportunityDto> one = templates is null ? [opp] : await new[] { opp }.WithStepsAsync(templates, ct);
        return Result.Ok((await one.WithConditionsAsync(db, workflows, ct))[0]);
    }
}
