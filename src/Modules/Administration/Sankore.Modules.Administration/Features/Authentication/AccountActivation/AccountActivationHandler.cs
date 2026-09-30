using MediatR;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Administration.Domain;
using Sankore.Modules.Administration.Infrastructure;
using Sankore.Modules.Administration.Infrastructure.Identity;
using Sankore.Shared.Kernel;

namespace Sankore.Modules.Administration.Features.Authentication.AccountActivation;

internal sealed class AccountActivationHandler(
    AdministrationDbContext db,
    UserManager<AppUser> userManager,
    ITenantContext tenantContext
) : IRequestHandler<AccountActivationCommand, Result<AccountActivationResult>>
{
    public async Task<Result<AccountActivationResult>> Handle(
        AccountActivationCommand request, CancellationToken ct)
    {
        // 1. Parse UserId
        if (!Guid.TryParse(request.UserId, out var userId))
            return Result.Fail<AccountActivationResult>("Invalid activation link.");

        // 2. Load with tracking so Activate() mutation is picked up by SaveChangesAsync
        var user = await db.Users
            .IgnoreQueryFilters()
            .AsTracking()
            .Where(u => u.Id == userId)
            .FirstOrDefaultAsync(ct);

        if (user is null)
            return Result.Fail<AccountActivationResult>("Invalid activation link.");

        // 3. Only PendingActivation accounts can be activated
        if (user.Status != UserStatus.PendingActivation)
            return Result.Fail<AccountActivationResult>(
                user.Status == UserStatus.Active
                    ? "Account is already active."
                    : "Account cannot be activated.");

        // 4. Validate the activation token. It lives in its own provider, so ResetPasswordAsync —
        //    which is hardwired to the password-reset provider — cannot check it for us.
        var isValid = await userManager.VerifyUserTokenAsync(
            user,
            ActivationTokens.ProviderName,
            ActivationTokens.Purpose,
            request.Token);

        if (!isValid)
            return Result.Fail<AccountActivationResult>(
                "Activation link has expired or already been used.");

        // 5. Set the initial password through Identity's own pipeline (strength validators,
        //    hashing, security-stamp rotation) with an internal token the caller never sees —
        //    the same idiom as AdminResetPasswordHandler. The stamp rotation is what makes the
        //    activation token single-use: it is baked into the token, so the link just consumed
        //    stops verifying from here on.
        var resetToken = await userManager.GeneratePasswordResetTokenAsync(user);
        var resetResult = await userManager.ResetPasswordAsync(user, resetToken, request.NewPassword);
        if (!resetResult.Succeeded)
        {
            var errors = string.Join("; ", resetResult.Errors.Select(e => e.Description));
            return Result.Fail<AccountActivationResult>(errors);
        }

        // 6. Transition domain status to Active
        user.Activate();
        db.Users.Update(user);
        // 7. Update password histories
        db.PasswordHistories.Add(PasswordHistory.Create(tenantContext.CurrentTenantId, user.Id, user.PasswordHash!));
        await db.SaveChangesAsync(ct);

        // AuditBehavior writes the AuditEntry automatically (ICommand marker).
        return Result.Ok(new AccountActivationResult(true, "Account activated successfully."));
    }
}
