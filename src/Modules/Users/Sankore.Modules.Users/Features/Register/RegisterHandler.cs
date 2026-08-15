using MediatR;
using Microsoft.AspNetCore.Identity;
using Sankore.Modules.Users.Domain;
using Sankore.Modules.Users.Infrastructure;
using Sankore.Shared.Kernel;

namespace Sankore.Modules.Users.Features.Register;

internal sealed class RegisterHandler(
    UserManager<AppUser> userManager) : IRequestHandler<RegisterCommand, Result<RegisterResult>>
{
    public async Task<Result<RegisterResult>> Handle(RegisterCommand request, CancellationToken cancellationToken)
    {
        var user = AppUser.Create(
            request.TenantId,
            request.AgencyId,
            request.FullName,
            request.Email);

        var createResult = await userManager.CreateAsync(user, request.Password);
        if (!createResult.Succeeded)
            return Result.Fail<RegisterResult>(string.Join("; ", createResult.Errors.Select(e => e.Description)));

        var roleResult = await userManager.AddToRoleAsync(user, Roles.Administrator);
        if (!roleResult.Succeeded)
            return Result.Fail<RegisterResult>(string.Join("; ", roleResult.Errors.Select(e => e.Description)));

        return Result.Ok(new RegisterResult(user.Id));
    }
}