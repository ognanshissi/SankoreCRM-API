namespace Sankore.Modules.Leads.Features.ExportLeads;

using MediatR;
using Sankore.Modules.Leads.Domain;
using Sankore.Shared.Kernel;

public sealed record ExportLeadsQuery(
    LeadStatus? Status,
    PipelineStage? PipelineStage,
    LeadSource? Source,
    Guid? OwnerId,
    Guid? AgencyId,
    string? Search,
    string? Tag
) : IRequest<Result<byte[]>>;
