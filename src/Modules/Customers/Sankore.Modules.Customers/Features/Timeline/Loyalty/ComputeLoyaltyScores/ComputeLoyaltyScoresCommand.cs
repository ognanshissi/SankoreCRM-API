namespace Sankore.Modules.Customers.Features.Timeline.Loyalty.ComputeLoyaltyScores;

using MediatR;
using Sankore.Shared.Infrastructure.Behaviors;
using Sankore.Shared.Kernel;

/// <summary>
/// Recomputes and historizes the loyalty score of every live client of one tenant
/// (US-M01-BE-28).
///
/// <see cref="ICommand"/>: transactional and audited under the SYSTEM account — a loyalty
/// score can drive pricing or eligibility, so each automated recomputation is traceable.
/// </summary>
public sealed record ComputeLoyaltyScoresCommand(Guid TenantId)
    : IRequest<Result<ComputeLoyaltyScoresResult>>, ICommand, IResourceCommand
{
    public string ResourceType => "ClientLoyaltyScore";
    public string? ResourceId => TenantId.ToString("D");
}

/// <param name="ClientsScored">Live clients scored.</param>
/// <param name="ProvisionalCount">Scores flagged provisional (client younger than 90 days).</param>
/// <param name="UnavailableComponents">Components that scored 0 because their module is missing.</param>
public sealed record ComputeLoyaltyScoresResult(
    int ClientsScored,
    int ProvisionalCount,
    IReadOnlyList<string> UnavailableComponents);
