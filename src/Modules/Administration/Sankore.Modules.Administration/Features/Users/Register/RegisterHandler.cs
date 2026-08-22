using MediatR;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using Sankore.Modules.Administration.Domain;
using Sankore.Modules.Administration.Domain.Events;
using Sankore.Modules.Administration.Infrastructure;
using Sankore.Shared.Infrastructure.Messaging;
using Sankore.Shared.Kernel;

namespace Sankore.Modules.Administration.Features.Users.Register;

internal sealed class RegisterHandler(
    UserManager<AppUser> userManager,
    AdministrationDbContext db,
    ITenantContext tenant,
    [FromKeyedServices(nameof(AdministrationDbContext))] IEventPublisher publisher) : IRequestHandler<RegisterCommand, Result<RegisterResult>>
{
    public async Task<Result<RegisterResult>> Handle(RegisterCommand request, CancellationToken cancellationToken)
    {
        var user = AppUser.CreateRoot(
            tenant.CurrentTenantId,
           $"{request.FirstName} {request.LastName}",
            request.Email);

        var createResult = await userManager.CreateAsync(user, request.Password);
        if (!createResult.Succeeded)
            return Result.Fail<RegisterResult>(string.Join("; ", createResult.Errors.Select(e => e.Description)));

        var roleResult = await userManager.AddToRoleAsync(user, Roles.System.Code);
        if (!roleResult.Succeeded)
            return Result.Fail<RegisterResult>(string.Join("; ", roleResult.Errors.Select(e => e.Description)));
        
        // Provision default profile
        var profile = UserProfile.Create(tenant.CurrentTenantId, user.Id);
        await db.AddAsync(profile, cancellationToken);

        await db.SaveChangesAsync(cancellationToken);
        
        await publisher.PublishAsync(
            new UserCreatedEvent(tenant.CurrentTenantId, user.Id, user.Email!, user.FullName), cancellationToken);
        
        return Result.Ok(new RegisterResult(user.Id));
    }
}