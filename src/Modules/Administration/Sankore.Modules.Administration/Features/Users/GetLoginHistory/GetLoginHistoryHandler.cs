namespace Sankore.Modules.Administration.Features.Users.GetLoginHistory;

using MediatR;
using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Administration.Infrastructure;
using Sankore.Shared.Kernel;

internal sealed class GetLoginHistoryHandler(AdministrationDbContext db)
    : IRequestHandler<GetLoginHistoryQuery, Result<IReadOnlyList<LoginHistoryDto>>>
{
    public async Task<Result<IReadOnlyList<LoginHistoryDto>>> Handle(
        GetLoginHistoryQuery query, CancellationToken ct)
    {
        var userExists = await db.Users.AnyAsync(u => u.Id == query.UserId, ct);
        if (!userExists)
            return Result.Fail<IReadOnlyList<LoginHistoryDto>>("USER_NOT_FOUND");

        var safePage = Math.Max(1, query.Page);
        var safeSize = Math.Clamp(query.PageSize, 1, 100);

        var items = await db.UserLoginLocations
            .Where(l => l.UserId == query.UserId)
            .OrderByDescending(l => l.OccuredAt)
            .Skip((safePage - 1) * safeSize)
            .Take(safeSize)
            .Select(l => new LoginHistoryDto(
                l.Id,
                l.OccuredAt,
                l.Location != null ? l.Location.Latitude : null,
                l.Location != null ? l.Location.Longitude : null))
            .ToListAsync(ct);

        return Result.Ok<IReadOnlyList<LoginHistoryDto>>(items);
    }
}
