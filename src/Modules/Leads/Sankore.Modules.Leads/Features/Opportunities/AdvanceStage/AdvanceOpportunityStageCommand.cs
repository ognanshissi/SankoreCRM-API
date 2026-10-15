namespace Sankore.Modules.Leads.Features.Opportunities.AdvanceStage;

using MediatR;
using Sankore.Modules.Leads.Domain;
using Sankore.Shared.Infrastructure.Behaviors;
using Sankore.Shared.Kernel;

/// <summary>
/// Moves a request to another OPEN stage.
///
/// <para>
/// Closing is deliberately NOT reachable here — see the endpoint. No <c>TenantId</c> either: the
/// global query filter on <c>Opportunity</c> already scopes the read, and a tenant on the command
/// would be a second, caller-supplied answer to a question the context already answers.
/// </para>
/// </summary>
internal sealed record AdvanceOpportunityStageCommand(
    Guid OpportunityId,
    OpportunityStage NewStage,
    /// <summary>UpdatedAt the caller last read; null skips the staleness check.</summary>
    DateTimeOffset? ExpectedUpdatedAt = null)
    : IRequest<Result>, ICommand, IResourceCommand
{
    public string ResourceType => "Opportunity";
    public string? ResourceId => OpportunityId.ToString();
}
