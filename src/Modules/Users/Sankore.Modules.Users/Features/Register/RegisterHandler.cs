using MediatR;
using Microsoft.AspNetCore.Identity;
using Sankore.Modules.Users.Domain;
using Sankore.Modules.Users.Infrastructure;
using Sankore.Shared.Kernel;

namespace Sankore.Modules.Users.Features.Register;

public class RegisterHandler(UsersDbContext db, UserManager<AppUser> userManager): IRequestHandler<RegisterCommand, Result<RegisterResult>>
{
    public async Task<Result<RegisterResult>> Handle(RegisterCommand request, CancellationToken cancellationToken)
    {
        // Start transaction
        var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        
        var user = AppUser.Create(
            request.TenantId, 
            request.AgencyId,
            request.FullName,
            request.Email);
        
        IdentityResult identityResult = await userManager.CreateAsync(user, request.Password);
        if (!identityResult.Succeeded)
        {
            return Result.Fail<RegisterResult>(identityResult.ToString());
        }
        
        IdentityResult identityRoleResult = await userManager.AddToRoleAsync(user, Roles.Administrator);
        if (!identityRoleResult.Succeeded)
            return Result.Fail<RegisterResult>(identityRoleResult.Errors.First().ToString() ?? "");
        
        transaction.Commit();

        return Result.Ok(new RegisterResult(user.Id));
    }
}