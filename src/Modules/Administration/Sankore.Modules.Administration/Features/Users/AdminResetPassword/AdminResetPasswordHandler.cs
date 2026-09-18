using MediatR;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Administration.Domain;
using Sankore.Modules.Administration.Infrastructure;
using Sankore.Shared.Infrastructure.Auth;
using Sankore.Shared.Kernel;

namespace Sankore.Modules.Administration.Features.Users.AdminResetPassword;

internal sealed class AdminResetPasswordHandler(
    AdministrationDbContext db,
    UserManager<AppUser> userManager,
    IPasswordHasher<AppUser> passwordHasher,
    ICurrentUser currentUser
) : IRequestHandler<AdminResetPasswordCommand, Result>
{
    private const int PasswordHistoryDepth = 12;

    public async Task<Result> Handle(AdminResetPasswordCommand request, CancellationToken ct)
    {
        // Global query filter scopes the lookup to the caller's tenant automatically.
        var target = await db.Users
            .AsTracking()
            .FirstOrDefaultAsync(u => u.Id == request.TargetUserId, ct);

        if (target is null)
            return Result.Fail("User not found.");

        // Admins cannot reset their own password through this endpoint.
        if (target.Id == currentUser.Id)
            return Result.Fail("Use the change-password endpoint to update your own password.");

        if (target.Status == UserStatus.Disabled)
            return Result.Fail("Cannot reset password for a disabled user. Re-activate the account first.");

        // Check password-reuse policy against the last N hashes.
        var recentHashes = await db.PasswordHistories
            .Where(p => p.UserId == target.Id)
            .OrderByDescending(p => p.SetAt)
            .Take(PasswordHistoryDepth)
            .Select(p => p.PasswordHash)
            .ToListAsync(ct);

        foreach (var historicHash in recentHashes)
        {
            var verification = passwordHasher.VerifyHashedPassword(
                target, historicHash, request.NewPassword);

            if (verification != PasswordVerificationResult.Failed)
                return Result.Fail("PASSWORD_RECENTLY_USED");
        }

        // Generate an internal token — goes through Identity's full pipeline
        // (strength validators, security stamp rotation) without exposing the token.
        var token  = await userManager.GeneratePasswordResetTokenAsync(target);
        var result = await userManager.ResetPasswordAsync(target, token, request.NewPassword);

        if (!result.Succeeded)
        {
            var errors = string.Join("; ", result.Errors.Select(e => e.Description));
            return Result.Fail(errors);
        }

        // Record in history, then immediately expire the password so the user is
        // forced to change it on their next login.
        db.PasswordHistories.Add(PasswordHistory.Create(target.TenantId, target.Id, target.PasswordHash!));
        target.ExpirePasswordNow();
        db.Users.Update(target);

        await db.SaveChangesAsync(ct);
        return Result.Ok();
    }
}
