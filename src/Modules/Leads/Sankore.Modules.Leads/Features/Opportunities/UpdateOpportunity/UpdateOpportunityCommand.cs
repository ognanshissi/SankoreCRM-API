namespace Sankore.Modules.Leads.Features.Opportunities.UpdateOpportunity;

using MediatR;
using Sankore.Shared.Infrastructure.Behaviors;
using Sankore.Shared.Kernel;

/// <summary>
/// Patches a request's editable fields. Every field is optional and <b>null leaves alone</b> — the
/// aggregate's own rule, so a screen that posts only what it collected cannot erase the rest.
/// </summary>
internal sealed record UpdateOpportunityCommand(
    Guid OpportunityId,
    string? Title = null,
    string? Description = null,
    decimal? EstimatedAmount = null,
    string? EstimatedCurrency = null,
    DateTimeOffset? ExpectedCloseDate = null,
    Guid? OwnerId = null,
    DateTimeOffset? ExpectedUpdatedAt = null)
    : IRequest<Result>, ICommand, IResourceCommand
{
    public string ResourceType => "Opportunity";
    public string? ResourceId => OpportunityId.ToString();
}
