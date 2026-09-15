using MediatR;
using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Administration.Domain;
using Sankore.Modules.Administration.Infrastructure;
using Sankore.Shared.Kernel;

namespace Sankore.Modules.Administration.Features.Users.GetUserStatusStats;

internal sealed class GetUserStatusStatsHandler(AdministrationDbContext db)
    : IRequestHandler<GetUserStatusStatsQuery, Result<UserStatusStatsDto>>
{
    public async Task<Result<UserStatusStatsDto>> Handle(
        GetUserStatusStatsQuery request, CancellationToken ct)
    {
        var counts = await db.Users
            .Where(u => u.AccountType == UserAccountType.Standard)
            .GroupBy(u => u.Status)
            .Select(g => new { Status = g.Key, Count = g.Count() })
            .ToListAsync(ct);

        var byStatus = counts.ToDictionary(x => x.Status, x => x.Count);

        return Result.Ok(new UserStatusStatsDto(
            Total:             counts.Sum(x => x.Count),
            Active:            byStatus.GetValueOrDefault(UserStatus.Active),
            PendingActivation: byStatus.GetValueOrDefault(UserStatus.PendingActivation),
            Disabled:          byStatus.GetValueOrDefault(UserStatus.Disabled),
            Locked:            byStatus.GetValueOrDefault(UserStatus.Locked)));
    }
}
