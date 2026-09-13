using MediatR;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Administration.Domain;
using Sankore.Modules.Administration.Infrastructure;
using Sankore.Shared.Kernel;

namespace Sankore.Modules.Administration.Features.Authentication.ResetPassword;

internal sealed class ResetPasswordHandler(
    AdministrationDbContext db,
    UserManager<AppUser> userManager,
    IPasswordHasher<AppUser> passwordHasher
) : IRequestHandler<ResetPasswordCommand, Result<ResetPasswordResult>>
{
    private const int PasswordHistoryDepth = 12;
    
    public async Task<Result<ResetPasswordResult>> Handle(
        ResetPasswordCommand request, CancellationToken ct)
    {
        // 1. Parse UserId
        if (!Guid.TryParse(request.UserId, out var userId))
            return Result.Fail<ResetPasswordResult>("Invalid reset link.");

        // 2. Load with tracking — user can belong to any tenant (link arrives via email, no JWT)
        var user = await db.Users
            .IgnoreQueryFilters()
            .AsTracking()
            .Where(u => u.Id == userId)
            .FirstOrDefaultAsync(ct);

        if (user is null)
            return Result.Fail<ResetPasswordResult>("Invalid reset link.");

        // 3. Only Active users can reset their password
        //    PendingActivation → use the activation flow instead
        //    Disabled → account must be re-enabled by an admin first
        if (user.Status != UserStatus.Active)
            return Result.Fail<ResetPasswordResult>(
                "Password reset is not available for this account. Contact your administrator.");
        
        if (user.Status == UserStatus.Disabled)
            return Result.Fail<ResetPasswordResult>("Cannot reset password for a disabled user.");

        
        // 2. Fetch the last N hashes — ordered newest-first to bail out early on recent reuse.
        var recentHashes = await db.PasswordHistories
            .Where(p => p.UserId == user.Id)
            .OrderByDescending(p => p.SetAt)
            .Take(PasswordHistoryDepth)
            .Select(p => p.PasswordHash)
            .ToListAsync(ct);

        // 3. Check each historic hash against the proposed new password.
        //    VerifyHashedPassword returns Failed / Success / SuccessRehashNeeded.
        //    Any non-Failed result means the password was recently used.
        foreach (var historicHash in recentHashes)
        {
            var verificationResult = passwordHasher.VerifyHashedPassword(
                user, historicHash, request.NewPassword);

            if (verificationResult != PasswordVerificationResult.Failed)
                return Result.Fail<ResetPasswordResult>("PASSWORD_RECENTLY_USED");
        }

        // 4. Validate token + set the new password via Identity
        var resetResult = await userManager.ResetPasswordAsync(user, request.Token, request.NewPassword);
        if (!resetResult.Succeeded)
        {
            var errors = string.Join("; ", resetResult.Errors.Select(e => e.Description));
            return Result.Fail<ResetPasswordResult>(errors);
        }

        // 5. Record password in history and refresh expiry
        db.PasswordHistories.Add(PasswordHistory.Create(user.TenantId, user.Id, user.PasswordHash!));

        // 6. Extend password expiry from today (token consumption resets the clock)
        user.ExtendPasswordExpiry();
        db.Users.Update(user);

        await db.SaveChangesAsync(ct);

        return Result.Ok(new ResetPasswordResult("Password reset successfully."));
    }
}
