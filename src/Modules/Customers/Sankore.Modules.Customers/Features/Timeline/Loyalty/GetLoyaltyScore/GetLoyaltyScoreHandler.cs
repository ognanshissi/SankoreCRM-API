namespace Sankore.Modules.Customers.Features.Timeline.Loyalty.GetLoyaltyScore;

using MediatR;
using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Customers.Domain;
using Sankore.Modules.Customers.Features.Timeline.Shared;
using Sankore.Modules.Customers.Infrastructure;
using Sankore.Shared.Infrastructure.Auth;
using Sankore.Shared.Kernel;
using Sankore.Shared.Kernel.Authorization;

internal sealed class GetLoyaltyScoreHandler(
    CustomersDbContext db,
    IAgencyScopeProvider agencyScope,
    ICurrentUser currentUser)
    : IRequestHandler<GetLoyaltyScoreQuery, Result<ClientLoyaltyScoreDto>>
{
    private const int MaxHistory = 50;

    private static readonly string[] UnavailableComponents = ["volume", "products"];

    public async Task<Result<ClientLoyaltyScoreDto>> Handle(
        GetLoyaltyScoreQuery request, CancellationToken ct)
    {
        var readable = await TimelineClientScope.CanReadClientAsync(
            db, agencyScope, currentUser, request.ClientId, ct);

        if (!readable)
            return Result.Fail<ClientLoyaltyScoreDto>(CustomerErrors.ClientNotFound);

        var limit = Math.Clamp(request.HistoryLimit <= 0 ? 12 : request.HistoryLimit, 1, MaxHistory);

        var history = await db.ClientLoyaltyScores
            .Where(s => s.ClientId == request.ClientId)
            .OrderByDescending(s => s.ComputedAt)
            .Take(limit)
            .Select(s => new { s.Score, s.IsProvisional, s.ComputedAt, s.BreakdownJson })
            .ToListAsync(ct);

        var latest = history.FirstOrDefault();

        return Result.Ok(new ClientLoyaltyScoreDto(
            ClientId: request.ClientId,
            Score: latest?.Score,
            // A client never scored yet is not "provisional", it is "unknown" — Score stays null.
            IsProvisional: latest?.IsProvisional ?? false,
            ComputedAt: latest?.ComputedAt,
            UnavailableComponents: UnavailableComponents,
            Breakdown: latest?.BreakdownJson,
            History: history
                .Select(h => new LoyaltyScoreHistoryItemDto(h.Score, h.IsProvisional, h.ComputedAt))
                .ToList()));
    }
}
