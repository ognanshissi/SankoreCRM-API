namespace Sankore.Modules.Leads.Features.Opportunities.CloseOpportunity;

using MediatR;
using Sankore.Modules.Leads.Domain;
using Sankore.Shared.Infrastructure.Behaviors;
using Sankore.Shared.Kernel;

/// <summary>
/// Closes a request, won or lost — and, when it is won, opens the draft contract it owes.
/// </summary>
internal sealed record CloseOpportunityCommand(
    Guid OpportunityId,
    OpportunityStage Outcome,
    string? Reason = null,
    DateTimeOffset? ExpectedUpdatedAt = null)
    : IRequest<Result<CloseOpportunityResult>>, ICommand, IResourceCommand
{
    public string ResourceType => "Opportunity";
    public string? ResourceId => OpportunityId.ToString();
}

/// <param name="HoldingId">
/// The draft contract the closing opened, when it opened one. <b>Null is an ordinary answer</b>
/// and never an error: a lost request opens nothing, a request attached to no client has no file
/// to open one on, and a product the catalogue does not recognise cannot be subscribed. The screen
/// uses it to decide between "voir la souscription" and "souscrire à la main".
/// </param>
internal sealed record CloseOpportunityResult(Guid? HoldingId);
