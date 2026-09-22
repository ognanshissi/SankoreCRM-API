using MediatR;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Administration.Domain;
using Sankore.Modules.Administration.Infrastructure;
using Sankore.Shared.Infrastructure.Auth;
using Sankore.Shared.Kernel;

namespace Sankore.Modules.Administration.Features.Users.ChangePassword;

internal sealed class ChangePasswordHandler(
    AdministrationDbContext db,
    UserManager<AppUser> userManager,
    IPasswordHasher<AppUser> passwordHasher,
    ICurrentUser currentUser
) : IRequestHandler<ChangePasswordCommand, Result>
{
    private const int PasswordHistoryDepth = 12;

    public async Task<Result> Handle(ChangePasswordCommand request, CancellationToken ct)
    {
        var user = await db.Users
            .AsTracking()
            .FirstOrDefaultAsync(u => u.Id == currentUser.Id, ct);

        if (user is null)
            return Result.Fail("User not found.");

        if (user.Status != UserStatus.Active)
            return Result.Fail("Password change is not available for this account.");

        // Verify current password before allowing change
        var checkResult = await userManager.CheckPasswordAsync(user, request.CurrentPassword);
        if (!checkResult)
            return Result.Fail("CURRENT_PASSWORD_INCORRECT");

        // Check password-reuse policy against the last N hashes.
        var recentHashes = await db.PasswordHistories
            .Where(p => p.UserId == user.Id)
            .OrderByDescending(p => p.SetAt)
            .Take(PasswordHistoryDepth)
            .Select(p => p.PasswordHash)
            .ToListAsync(ct);

        foreach (var historicHash in recentHashes)
        {
            var verification = passwordHasher.VerifyHashedPassword(
                user, historicHash, request.NewPassword);

            if (verification != PasswordVerificationResult.Failed)
                return Result.Fail("PASSWORD_RECENTLY_USED");
        }

        // Generate an internal token so we go through Identity's full pipeline
        // (password strength validators, security stamp refresh, etc.) without
        // exposing the token to the caller.
        var token  = await userManager.GeneratePasswordResetTokenAsync(user);
        var result = await userManager.ResetPasswordAsync(user, token, request.NewPassword);

        if (!result.Succeeded)
        {
            var errors = string.Join("; ", result.Errors.Select(e => e.Description));
            return Result.Fail(errors);
        }

        // Record in history and extend expiry.
        db.PasswordHistories.Add(PasswordHistory.Create(user.TenantId, user.Id, user.PasswordHash!));
        user.ExtendPasswordExpiry();
        db.Users.Update(user);

        await db.SaveChangesAsync(ct);
        return Result.Ok();
    }
}
