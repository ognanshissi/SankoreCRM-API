using MediatR;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Administration.Domain;
using Sankore.Modules.Administration.Infrastructure;
using Sankore.Modules.Administration.Infrastructure.Identity;
using Sankore.Shared.Kernel;

namespace Sankore.Modules.Administration.Features.Authentication.AccountActivation;

internal sealed class ValidateActivationTokenHandler(
    AdministrationDbContext db,
    ITenantContext tenantContext,
    UserManager<AppUser> userManager)
    : IRequestHandler<ValidateActivationTokenQuery, Result>
{
    public async Task<Result> Handle(ValidateActivationTokenQuery request, CancellationToken ct)
    {
        if (!Guid.TryParse(request.UserId, out var userId))
            return Result.Fail("Invalid activation link.");

        var user = await db.Users
            .IgnoreQueryFilters()
            .Where(u => u.Id == userId && u.TenantId == tenantContext.CurrentTenantId)
            .FirstOrDefaultAsync(ct);

        if (user is null)
            return Result.Fail("Invalid activation link.");

        if (user.Status == UserStatus.Active)
            return Result.Fail("Account is already active.");

        if (user.Status != UserStatus.PendingActivation)
            return Result.Fail("Account cannot be activated.");

        // VerifyUserTokenAsync checks validity without consuming the token — AccountActivationHandler
        // consumes it. Provider and purpose must match CreateUserHandler exactly: both are part of
        // the token's purpose chain, so a mismatch reads as an invalid token, not as a bug.
        var isValid = await userManager.VerifyUserTokenAsync(
            user,
            ActivationTokens.ProviderName,
            ActivationTokens.Purpose,
            request.Token);

        return isValid
            ? Result.Ok()
            : Result.Fail("Activation link has expired or already been used.");
    }
}
