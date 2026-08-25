using MediatR;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Administration.Domain;
using Sankore.Modules.Administration.Infrastructure;
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

        // 4. Validate token + set the initial password atomically via Identity
        //    ResetPasswordAsync validates the token, hashes the password, and calls UpdateAsync internally.
        var resetResult = await userManager.ResetPasswordAsync(user, request.Token, request.NewPassword);
        if (!resetResult.Succeeded)
        {
            var errors = string.Join("; ", resetResult.Errors.Select(e => e.Description));
            return Result.Fail<AccountActivationResult>(errors);
        }

        // 5. Transition domain status to Active
        user.Activate();
        db.Users.Update(user);
        // 6. Update password histories
        db.PasswordHistories.Add(PasswordHistory.Create(tenantContext.CurrentTenantId, user.Id, user.PasswordHash!));
        await db.SaveChangesAsync(ct);

        // AuditBehavior writes the AuditEntry automatically (ICommand marker).
        return Result.Ok(new AccountActivationResult(true, "Account activated successfully."));
    }
}
