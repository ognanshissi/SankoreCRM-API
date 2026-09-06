using MediatR;
using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Administration.Features.Users.GetUser;
using Sankore.Modules.Administration.Infrastructure;
using Sankore.Shared.Kernel;

namespace Sankore.Modules.Administration.Features.Users.ListUsers;

internal sealed class ListUsersHandler(
    AdministrationDbContext db
) : IRequestHandler<ListUsersQuery, Result<ListUsersResult>>
{
    public async Task<Result<ListUsersResult>> Handle(ListUsersQuery request, CancellationToken ct)
    {
        var query = db.Users.Include(u => u.Agency).AsQueryable();

        if (request.Status.HasValue)
            query = query.Where(u => u.Status == request.Status.Value);

        if (request.AgencyId.HasValue)
            query = query.Where(u => u.AgencyId == request.AgencyId.Value);

        if (!string.IsNullOrWhiteSpace(request.Search))
        {
            var term = request.Search.ToLower();
            query = query.Where(u =>
                u.FullName.ToLower().Contains(term) ||
                u.Email!.ToLower().Contains(term));
        }

        var totalCount = await query.CountAsync(ct);

        var items = await query
            .OrderBy(u => u.FullName)
            .Skip((request.Page - 1) * request.PageSize)
            .Take(request.PageSize)
            .Select(u => new UserDto(
                u.Id,
                u.FullName,
                u.Email!,
                u.Status.ToString(),
                u.AgencyId,
                u.Agency != null ? u.Agency.Name : null,
                u.MfaEnabled,
                u.PasswordExpiresAt,
                u.LastLoginAt,
                u.DeactivatedAt,
                u.SpokenLanguages,
                u.Specialties,
                u.IsAvailable,
                u.EnableNotifications,
                u.AccountType.ToString()))
            .ToListAsync(ct);

        return Result.Ok(new ListUsersResult(items, totalCount, request.Page, request.PageSize));
    }
}
