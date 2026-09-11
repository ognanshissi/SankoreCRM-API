using MediatR;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Administration.Domain;
using Sankore.Modules.Administration.Infrastructure;
using Sankore.Shared.Kernel;

namespace Sankore.Modules.Administration.Features.Authentication.AccountActivation;

internal sealed class ValidateActivationTokenHandler(
    AdministrationDbContext db,
    UserManager<AppUser> userManager)
    : IRequestHandler<ValidateActivationTokenQuery, Result>
{
    public async Task<Result> Handle(ValidateActivationTokenQuery request, CancellationToken ct)
    {
        if (!Guid.TryParse(request.UserId, out var userId))
            return Result.Fail("Invalid activation link.");

        var user = await db.Users
            .IgnoreQueryFilters()
            .Where(u => u.Id == userId)
            .FirstOrDefaultAsync(ct);

        if (user is null)
            return Result.Fail("Invalid activation link.");

        if (user.Status == UserStatus.Active)
            return Result.Fail("Account is already active.");

        if (user.Status != UserStatus.PendingActivation)
            return Result.Fail("Account cannot be activated.");

        // VerifyUserTokenAsync checks validity without consuming the token.
        // The token will only be consumed by ResetPasswordAsync in AccountActivationHandler.
        var isValid = await userManager.VerifyUserTokenAsync(
            user,
            userManager.Options.Tokens.PasswordResetTokenProvider,
            UserManager<AppUser>.ResetPasswordTokenPurpose,
            request.Token);

        return isValid
            ? Result.Ok()
            : Result.Fail("Activation link has expired or already been used.");
    }
}
