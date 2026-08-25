using MediatR;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Administration.Domain;
using Sankore.Modules.Administration.Infrastructure;
using Sankore.Shared.Kernel;

namespace Sankore.Modules.Administration.Features.Authentication.ResetPassword;

internal sealed class ResetPasswordHandler(
    AdministrationDbContext db,
    UserManager<AppUser> userManager
) : IRequestHandler<ResetPasswordCommand, Result<ResetPasswordResult>>
{
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
