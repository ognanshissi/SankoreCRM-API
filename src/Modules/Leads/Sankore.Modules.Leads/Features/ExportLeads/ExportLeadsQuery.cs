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
    string? Tag,
    Guid? LeadSourceConfigId = null,
    // The list's filters, so exporting a view (« Chauds », « Mes leads », « En file d'attente »,
    // custom fields) exports the rows on screen and not every lead of the other filters.
    LeadIntentLevel? IntentLevel = null,
    bool? AwaitingDispatch = null,
    IReadOnlyList<string>? CustomFilters = null,
    Guid? AgentId = null
) : IRequest<Result<byte[]>>;
