using MediatR;
using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Administration.Domain;
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
        var query = db.Users.Where(x => x.AccountType == UserAccountType.Standard).Include(u => u.Agency).AsQueryable();

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

        var users = await query
            .Skip((request.Page - 1) * request.PageSize)
            .Take(request.PageSize)
            .Select(u => new
            {
                u.Id,
                u.FullName,
                u.Email,
                u.Status,
                u.AgencyId,
                AgencyName = u.Agency != null ? u.Agency.Name : null,
                u.MfaEnabled,
                u.PasswordExpiresAt,
                u.LastLoginAt,
                u.DeactivatedAt,
                u.SpokenLanguages,
                u.Specialties,
                u.IsAvailable,
                u.EnableNotifications,
                u.AccountType,
            })
            .ToListAsync(ct);

        var userIds = users.Select(u => u.Id).ToList();
        var rolesLookup = await db.UserRoles
            .Where(ur => userIds.Contains(ur.UserId) && ur.IsActive)
            .Join(db.Roles, ur => ur.RoleId, r => r.Id, (ur, r) => new { ur.UserId, r.Name })
            .ToListAsync(ct);

        var rolesByUser = rolesLookup
            .GroupBy(x => x.UserId)
            .ToDictionary(g => g.Key, g => g.Select(x => x.Name!).ToList());

        var items = users.Select(u => new UserDto(
                u.Id,
                u.FullName,
                u.Email!,
                u.Status.ToString(),
                u.AgencyId,
                u.AgencyName,
                u.MfaEnabled,
                u.PasswordExpiresAt,
                u.LastLoginAt,
                u.DeactivatedAt,
                u.SpokenLanguages,
                u.Specialties,
                u.IsAvailable,
                u.EnableNotifications,
                u.AccountType.ToString(),
                rolesByUser.TryGetValue(u.Id, out var r) ? r : []))
            .ToList();

        return Result.Ok(new ListUsersResult(items, totalCount, request.Page, request.PageSize));
    }
}
