using MediatR;
using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Administration.Infrastructure;
using Sankore.Shared.Kernel;

namespace Sankore.Modules.Administration.Features.Users.ReactivateUser;

/// <summary>
/// Restores a disabled account's Status — and deliberately nothing else.
///
/// DeactivateUserHandler revokes the user's role grants in both stores (Identity's, which
/// login reads, and the db.UserRoles audit mirror). Reactivation does NOT bring them back:
/// re-enabling an account is an explicit decision, and so is handing it its permissions
/// again. A reactivated user can sign in and has no roles until an administrator grants
/// them through AssignRole / BulkAssignRole.
///
/// Restoring automatically would also be unsound: the mirror records a revocation as
/// IsActive = false and cannot distinguish one caused by deactivation from one an
/// administrator made on purpose through RevokeRole, so a blind restore would resurrect
/// roles that were deliberately taken away. Telling those apart needs a marker on
/// UserRole, i.e. a migration — worth doing if reactivation ever has to be lossless.
/// </summary>
internal sealed class ReactivateUserHandler(
    AdministrationDbContext db
) : IRequestHandler<ReactivateUserCommand, Result>
{
    public async Task<Result> Handle(ReactivateUserCommand request, CancellationToken ct)
    {
        var user = await db.Users
            .AsTracking()
            .Where(u => u.Id == request.UserId)
            .SingleOrDefaultAsync(ct);

        if (user is null)
            return Result.Fail($"User {request.UserId} not found.");

        try
        {
            user.Reactivate();
        }
        catch (DomainException ex)
        {
            return Result.Fail(ex.Message);
        }

        await db.SaveChangesAsync(ct);

        return Result.Ok();
    }
}
