using MediatR;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Sankore.Modules.Administration.Domain;
using Sankore.Modules.Administration.Features.Users.Invitation;
using Sankore.Modules.Administration.Infrastructure.Identity;
using Sankore.Modules.Administration.Infrastructure;
using Sankore.Modules.Administration.PublicApi;
using Sankore.Modules.Notifications.PublicApi;
using Sankore.Shared.Infrastructure.Auth;
using Sankore.Shared.Infrastructure.Messaging;
using Sankore.Shared.Kernel;

namespace Sankore.Modules.Administration.Features.Users.CreateUser;

internal sealed class CreateUserHandler(
    AdministrationDbContext db,
    UserManager<AppUser> userManager,
    RoleManager<AppRole> roleManager,
    ITenantContext tenantContext,
    [FromKeyedServices(nameof(AdministrationDbContext))] IEventPublisher publisher,
    ActivationInvitationSender invitations,
    EmployeeNumberAllocator employeeNumbers
) : IRequestHandler<CreateUserCommand, Result<CreateUserResult>>
{
    public async Task<Result<CreateUserResult>> Handle(
        CreateUserCommand request, CancellationToken ct)
    {
        var tenantId = tenantContext.CurrentTenantId;

        // 1. Guard: agency must exist within this tenant
        var agencyExists = await db.Agencies
            .AnyAsync(a => a.Id == request.AgencyId, ct);

        if (!agencyExists)
            return Result.Fail<CreateUserResult>($"Agency {request.AgencyId} not found.");

        // 2. Guard: role must exist
        var role = await roleManager.FindByIdAsync(request.RoleId.ToString());
        if (role is null)
            return Result.Fail<CreateUserResult>($"Role {request.RoleId} not found.");

        // 3. Guard: email must be unique within tenant
        var normalizedEmail = request.Email.ToUpperInvariant();
        var emailTaken = await db.Users
            .IgnoreQueryFilters()
            .AnyAsync(u => u.TenantId == tenantId && u.NormalizedEmail == normalizedEmail, ct);

        if (emailTaken)
            return Result.Fail<CreateUserResult>("A user with this email already exists in this tenant.");

        // 4. Create the user aggregate (Status = PendingActivation, no password yet)
        var user = AppUser.Create(tenantId, request.AgencyId, request.FirstName, request.LastName, request.Email);
        user.RecordCreator(request.CallerUserId);
        user.SetEmployeeNumber(await employeeNumbers.NextAsync(tenantId, ct));

        var identityResult = await userManager.CreateAsync(user);
        if (!identityResult.Succeeded)
            return Result.Fail<CreateUserResult>(
                string.Join("; ", identityResult.Errors.Select(e => e.Description)));

        // 5. Assign role via Identity (required for JWT GetRolesAsync)
        var roleResult = await userManager.AddToRoleAsync(user, role.Name!);
        if (!roleResult.Succeeded)
            return Result.Fail<CreateUserResult>(
                string.Join("; ", roleResult.Errors.Select(e => e.Description)));

        // 6. Write UserRole with audit metadata (who assigned it, when)
        var userRole = UserRole.Assign(tenantId, user.Id, role.Id, request.CallerUserId);
        await db.AddAsync(userRole, ct);

        // 7. Provision default profile
        var profile = UserProfile.Create(tenantId, user.Id, request.DefaultLanguage);
        await db.AddAsync(profile, ct);

        await db.SaveChangesAsync(ct);

        // 8. Publish the event, then send the activation link (its own provider, not the
        //    password-reset one — see Identity:ActivationTokenLifespan).
        await publisher.PublishAsync(
            new UserCreatedEvent(tenantId, user.Id, user.Email!, user.FullName), ct);

        await invitations.SendAsync(user, request.DefaultLanguage, invalidatePrevious: false, ct);
        await db.SaveChangesAsync(ct);

        // AuditBehavior writes the AuditEntry automatically (ICommand marker).
        return Result.Ok(new CreateUserResult(user.Id));
    }
}
