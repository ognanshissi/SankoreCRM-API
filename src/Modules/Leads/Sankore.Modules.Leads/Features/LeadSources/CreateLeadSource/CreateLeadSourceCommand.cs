namespace Sankore.Modules.Leads.Features.LeadSources.CreateLeadSource;

using MediatR;
using Sankore.Modules.Leads.Domain;
using Sankore.Shared.Infrastructure.Behaviors;
using Sankore.Shared.Kernel;

internal sealed record CreateLeadSourceCommand(
    Guid TenantId,
    string Code,
    string Label,
    LeadChannelType ChannelType,
    int DisplayOrder,
    IntegrationMode? IntegrationMode = null,
    string? Description = null,
    SourceSettings? Settings = null,
    string? PlatformConnectionId = null,
    int DedupWindowDays = 30,
    decimal? CostPerLead = null,
    string? CostCurrency = null
) : IRequest<Result<Guid>>, ICommand, IResourceCommand
{
    public string ResourceType => "LeadSourceConfig";
    public string? ResourceId  => null;
}
