using MediatR;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Administration.Domain;
using Sankore.Modules.Administration.Infrastructure;
using Sankore.Shared.Kernel;

namespace Sankore.Modules.Administration.Features.Authentication.ResetPassword;

internal sealed class ValidateResetTokenHandler(
    AdministrationDbContext db,
    UserManager<AppUser> userManager)
    : IRequestHandler<ValidateResetTokenQuery, Result>
{
    /// <summary>
    /// One message for every rejection. A reset form is reachable without authentication, so
    /// distinguishing "no such user" from "expired token" would turn this endpoint into an
    /// account-enumeration oracle.
    /// </summary>
    private const string Rejected = "This reset link is invalid or has expired.";

    public async Task<Result> Handle(ValidateResetTokenQuery request, CancellationToken ct)
    {
        if (!Guid.TryParse(request.UserId, out var userId))
            return Result.Fail(Rejected);

        // No tenant filter, deliberately: ResetPasswordHandler looks the user up the same way,
        // because the link arrives by email and carries no JWT. Filtering here would let a link
        // pass validation and then fail on submit — or the reverse — and the whole point of this
        // query is to predict what the reset will do.
        var user = await db.Users
            .IgnoreQueryFilters()
            .Where(u => u.Id == userId)
            .FirstOrDefaultAsync(ct);

        if (user is null)
            return Result.Fail(Rejected);

        // Same gate as ResetPasswordHandler. A PendingActivation user belongs in the activation
        // flow and a Disabled one needs an administrator first; showing them a password form that
        // is guaranteed to fail helps nobody.
        if (user.Status != UserStatus.Active)
            return Result.Fail(
                "Password reset is not available for this account. Contact your administrator.");

        // VerifyUserTokenAsync checks the token without spending it; ResetPasswordAsync spends it.
        // Provider and purpose must be the exact pair ResetPasswordAsync uses internally — a
        // mismatch reads as an expired link rather than as a bug, which is the hardest kind of
        // defect to trace in a flow nobody can reproduce on demand.
        var isValid = await userManager.VerifyUserTokenAsync(
            user,
            userManager.Options.Tokens.PasswordResetTokenProvider,
            UserManager<AppUser>.ResetPasswordTokenPurpose,
            request.Token);

        return isValid ? Result.Ok() : Result.Fail(Rejected);
    }
}
