using MediatR;
using Microsoft.AspNetCore.Identity;
using Sankore.Modules.Administration.Domain;
using Sankore.Shared.Kernel;

namespace Sankore.Modules.Administration.Features.Users.Register;

internal sealed class RegisterHandler(
    UserManager<AppUser> userManager) : IRequestHandler<RegisterCommand, Result<RegisterResult>>
{
    public async Task<Result<RegisterResult>> Handle(RegisterCommand request, CancellationToken cancellationToken)
    {
        var user = AppUser.Create(
            request.TenantId,
            request.AgencyId,
           $"{request.FirstName} {request.LastName}",
            request.Email);

        var createResult = await userManager.CreateAsync(user, request.Password);
        if (!createResult.Succeeded)
            return Result.Fail<RegisterResult>(string.Join("; ", createResult.Errors.Select(e => e.Description)));

        var roleResult = await userManager.AddToRoleAsync(user, request.Role);
        if (!roleResult.Succeeded)
            return Result.Fail<RegisterResult>(string.Join("; ", roleResult.Errors.Select(e => e.Description)));

        // Create user profile
        // Emit user-created event
        return Result.Ok(new RegisterResult(user.Id));
    }
}