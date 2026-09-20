namespace Sankore.Modules.Leads.Features.Opportunities.CreateOpportunityForCustomer;

using MediatR;
using Sankore.Shared.Infrastructure.Behaviors;
using Sankore.Shared.Kernel;

internal sealed record CreateOpportunityForCustomerCommand(
    Guid TenantId,
    Guid CustomerEntityId,
    string Title,
    string Product,
    Guid? OwnerId = null,
    Guid? LeadId = null,
    string? Description = null,
    decimal? EstimatedAmount = null,
    string? EstimatedCurrency = null,
    DateTimeOffset? ExpectedCloseDate = null,
    string? CustomerPhone = null,
    bool Force = false
) : IRequest<Result<CreateOpportunityForCustomerResult>>, ICommand, IResourceCommand
{
    public string ResourceType => "Opportunity";
    public string? ResourceId  => null;
}

public sealed record CreateOpportunityForCustomerResult(
    Guid? OpportunityId,
    bool DuplicateDetected,
    IReadOnlyList<Guid>? ExistingOpportunityIds = null);
