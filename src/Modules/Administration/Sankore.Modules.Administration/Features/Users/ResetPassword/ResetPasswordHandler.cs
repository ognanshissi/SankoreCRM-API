using MediatR;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Administration.Domain;
using Sankore.Modules.Administration.Infrastructure;
using Sankore.Shared.Kernel;

namespace Sankore.Modules.Administration.Features.Users.ResetPassword;

internal sealed class ResetPasswordHandler(
    AdministrationDbContext db,
    UserManager<AppUser> userManager,
    IPasswordHasher<AppUser> passwordHasher
) : IRequestHandler<ResetPasswordCommand, Result>
{
    private const int PasswordHistoryDepth = 12;

    public async Task<Result> Handle(ResetPasswordCommand request, CancellationToken ct)
    {
        // 1. Load with tracking — we need to mutate PasswordExpiresAt after the reset.
        //    Tenant query filter applied automatically; no IgnoreQueryFilters needed here
        //    because this is an authenticated (admin) request.
        var user = await db.Users
            .AsTracking()
            .Where(u => u.Id == request.UserId)
            .SingleOrDefaultAsync(ct);

        if (user is null)
            return Result.Fail($"User {request.UserId} not found.");

        if (user.Status == UserStatus.Disabled)
            return Result.Fail("Cannot reset password for a disabled user.");

        // 2. Fetch the last N hashes — ordered newest-first to bail out early on recent reuse.
        var recentHashes = await db.PasswordHistories
            .Where(p => p.UserId == request.UserId)
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
                return Result.Fail("PASSWORD_RECENTLY_USED");
        }

        // 4. Capture the current hash BEFORE Identity overwrites it.
        //    We archive this so future resets can detect reuse of today's password.
        var currentHash = user.PasswordHash;

        // 5. Reset password via Identity (generates token + validates + hashes + persists).
        //    UserManager.ResetPasswordAsync calls SaveChangesAsync internally, which is safe
        //    because TransactionBehavior wraps the entire handler in a TransactionScope.
        var resetToken = await userManager.GeneratePasswordResetTokenAsync(user);
        var resetResult = await userManager.ResetPasswordAsync(user, resetToken, request.NewPassword);

        if (!resetResult.Succeeded)
            return Result.Fail(string.Join("; ", resetResult.Errors.Select(e => e.Description)));

        // 6. Archive the old hash so it participates in future reuse checks.
        if (!string.IsNullOrEmpty(currentHash))
            await db.PasswordHistories.AddAsync(
                PasswordHistory.Create(user.TenantId, user.Id, currentHash), ct);

        // 7. Extend password expiry from now (section 6.1 — 90-day rotation policy).
        //    user is still tracked after ResetPasswordAsync; this mutation is detected by EF.
        user.ExtendPasswordExpiry();

        await db.SaveChangesAsync(ct);

        // AuditBehavior writes AuditEntry automatically (ICommand marker).
        return Result.Ok();
    }
}
