using MediatR;
using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Administration.Domain;
using Sankore.Modules.Administration.Infrastructure;
using Sankore.Modules.Administration.Infrastructure.Sessions;
using Sankore.Shared.Kernel;

namespace Sankore.Modules.Administration.Features.Authentication.Logout;

internal sealed class LogoutHandler(AdministrationDbContext db)
    : IRequestHandler<LogoutCommand, Result>
{
    public async Task<Result> Handle(LogoutCommand request, CancellationToken ct)
    {
        // AsTracking: without it RecordLogout was never saved (the context reads untracked by default).
        var user = await db.Users.AsTracking().FirstOrDefaultAsync(u => u.Id == request.UserId, ct);
        if (user is null)
            return Result.Fail("User not found.");

        user.RecordLogout();

        await SessionTerminator.EndAsync(db, request.UserId, SessionEndReason.Logout, onlySessionId: request.SessionId, ct: ct);

        await db.SaveChangesAsync(ct);

        return Result.Ok();
    }
}