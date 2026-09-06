using MediatR;
using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Administration.Infrastructure;
using Sankore.Shared.Infrastructure.Auth;
using Sankore.Shared.Kernel;

namespace Sankore.Modules.Administration.Features.Users.GetUser;

internal sealed class GetUserHandler(
    AdministrationDbContext db,
    ICurrentUser currentUser
) : IRequestHandler<GetUserQuery, Result<UserDto>>
{
    public async Task<Result<UserDto>> Handle(GetUserQuery request, CancellationToken ct)
    {
        var user = await db.Users
            .Include(u => u.Agency)
            .Where(u => u.Id == request.UserId)
            .SingleOrDefaultAsync(ct);

        if (user is null)
            return Result.Fail<UserDto>($"User {request.UserId} not found.");

        return Result.Ok(new UserDto(
            user.Id,
            user.FullName,
            user.Email!,
            user.Status.ToString(),
            user.AgencyId,
            user.Agency?.Name,
            user.MfaEnabled,
            user.PasswordExpiresAt,
            user.LastLoginAt,
            user.DeactivatedAt,
            user.SpokenLanguages,
            user.Specialties,
            user.IsAvailable,
            user.EnableNotifications,
            user.AccountType.ToString()));
    }
}
