namespace Sankore.Modules.Leads.Features.Opportunities.CreateOpportunity;

using MediatR;
using Sankore.Shared.Infrastructure.Behaviors;
using Sankore.Shared.Kernel;

internal sealed record CreateOpportunityCommand(
    Guid TenantId,
    Guid LeadId,
    string Title,
    string Product,
    Guid? OwnerId = null,
    string? Description = null,
    decimal? EstimatedAmount = null,
    string? EstimatedCurrency = null,
    DateTimeOffset? ExpectedCloseDate = null
) : IRequest<Result<Guid>>, ICommand, IResourceCommand
{
    public string ResourceType => "Opportunity";
    public string? ResourceId  => null;
}
