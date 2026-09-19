namespace Sankore.Modules.Leads.Features.ListLeads;

using MediatR;
using Sankore.Modules.Leads.Domain;
using Sankore.Modules.Leads.Features.GetLead;
using Sankore.Shared.Kernel;

internal sealed record ListLeadsQuery(
    int Page = 1,
    int PageSize = 20,
    LeadStatus? Status = null,
    PipelineStage? PipelineStage = null,
    LeadSource? Source = null,
    Guid? OwnerId = null,
    Guid? AgencyId = null,
    string? Search = null,
    string? Tag = null,
    LeadIntentLevel? IntentLevel = null
) : IRequest<Result<PagedResult<LeadDto>>>;
