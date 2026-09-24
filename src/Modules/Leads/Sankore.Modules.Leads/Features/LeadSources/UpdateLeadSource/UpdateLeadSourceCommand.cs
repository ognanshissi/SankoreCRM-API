namespace Sankore.Modules.Leads.Features.LeadSources.UpdateLeadSource;

using MediatR;
using Sankore.Modules.Leads.Domain;
using Sankore.Shared.Infrastructure.Behaviors;
using Sankore.Shared.Kernel;

internal sealed record UpdateLeadSourceCommand(
    Guid SourceId,
    uint ExpectedVersion,
    string Label,
    int? DisplayOrder = null,
    string? Description = null,
    SourceSettings? Settings = null,
    string? PlatformConnectionId = null,
    int? DedupWindowDays = null,
    decimal? CostPerLead = null,
    string? CostCurrency = null,
    Guid? DefaultAgencyId = null,
    Guid? DefaultDispatchingRuleId = null
) : IRequest<Result>, ICommand, IResourceCommand
{
    public string ResourceType => "LeadSourceConfig";
    public string? ResourceId  => SourceId.ToString();
}
