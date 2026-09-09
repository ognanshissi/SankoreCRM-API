using MediatR;
using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Administration.Infrastructure;
using Sankore.Shared.Kernel;

namespace Sankore.Modules.Administration.Features.Authentication.Logout;

internal sealed class LogoutHandler(AdministrationDbContext db)
    : IRequestHandler<LogoutCommand, Result>
{
    public async Task<Result> Handle(LogoutCommand request, CancellationToken ct)
    {
        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == request.UserId, ct);
        if (user is null)
            return Result.Fail("User not found.");

        user.RecordLogout();

        var activeTokens = await db.RefreshTokens
            .AsTracking()
            .Where(r => r.UserId == request.UserId && r.RevokedAt == null)
            .ToListAsync(ct);

        foreach (var token in activeTokens)
            token.Revoke();

        await db.SaveChangesAsync(ct);

        return Result.Ok();
    }
}