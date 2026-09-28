namespace Sankore.Modules.Customers.Features.Timeline.Loyalty.GetLoyaltyScore;

using MediatR;
using Sankore.Shared.Kernel;

/// <summary>Current loyalty score of a client plus its recent history (US-M01-BE-28).</summary>
/// <param name="HistoryLimit">How many past computations to return, newest first. Clamped to 50.</param>
public sealed record GetLoyaltyScoreQuery(Guid ClientId, int HistoryLimit = 12)
    : IRequest<Result<ClientLoyaltyScoreDto>>;

/// <param name="Score">Latest score, 0..100. Null when the client has never been scored.</param>
/// <param name="IsProvisional">
/// True while the client is younger than 90 days: tenure and regularity cannot be measured
/// meaningfully yet, so the score must be shown as indicative.
/// </param>
/// <param name="UnavailableComponents">
/// Components that contributed 0 because their owning module is not wired
/// (<c>volume</c> → M03 Savings, <c>products</c> → M04 Credit).
/// </param>
/// <param name="Breakdown">Raw <c>BreakdownJson</c> of the latest computation, passed through verbatim.</param>
public sealed record ClientLoyaltyScoreDto(
    Guid ClientId,
    int? Score,
    bool IsProvisional,
    DateTimeOffset? ComputedAt,
    IReadOnlyList<string> UnavailableComponents,
    string? Breakdown,
    IReadOnlyList<LoyaltyScoreHistoryItemDto> History);

public sealed record LoyaltyScoreHistoryItemDto(int Score, bool IsProvisional, DateTimeOffset ComputedAt);
