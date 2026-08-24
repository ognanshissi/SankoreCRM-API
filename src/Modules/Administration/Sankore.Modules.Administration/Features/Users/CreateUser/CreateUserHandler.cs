using MediatR;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Sankore.Modules.Administration.Domain;
using Sankore.Modules.Administration.Domain.Events;
using Sankore.Modules.Administration.Infrastructure;
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
    INotificationsModule notifications
) : IRequestHandler<CreateUserCommand, Result<CreateUserResult>>
{
    public async Task<Result<CreateUserResult>> Handle(
        CreateUserCommand request, CancellationToken ct)
    {
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
        var emailTaken = await db.Users
            .IgnoreQueryFilters()
            .AnyAsync(u => u.TenantId == tenantContext.CurrentTenantId
                           && u.NormalizedEmail != null
                           && u.NormalizedEmail.Equals(request.Email), ct);

        if (emailTaken)
            return Result.Fail<CreateUserResult>("A user with this email already exists in this tenant.");

        // 4. Create the user aggregate (Status = PendingActivation, no password yet)
        var user = AppUser.Create(tenantContext.CurrentTenantId, request.AgencyId, request.FullName, request.Email);

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
        var userRole = UserRole.Assign(tenantContext.CurrentTenantId, user.Id, role.Id, request.CallerUserId);
        await db.AddAsync(userRole, ct);

        // 7. Provision default profile
        var profile = UserProfile.Create(tenantContext.CurrentTenantId, user.Id, request.DefaultLanguage);
        await db.AddAsync(profile, ct);

        await db.SaveChangesAsync(ct);

        // 8. Generate activation token and publish event — email service sends the activation link
        var activationToken = await userManager.GeneratePasswordResetTokenAsync(user);

        await publisher.PublishAsync(
            new UserCreatedEvent(tenantContext.CurrentTenantId, user.Id, user.Email!, user.FullName), ct);

        // 9. Queue activation email — runs after SaveChangesAsync so the user row is committed
        await notifications.QueueEmailAsync(new QueueEmailRequest(
            TemplateKey:    "user.activation",
            RecipientEmail: user.Email!,
            RecipientName:  user.FullName,
            Module:         "Administration",
            Locale:         request.DefaultLanguage,
            TemplateData: new Dictionary<string, object>
            {
                ["full_name"]        = user.FullName,
                ["activation_token"] = activationToken,
                ["tenant_id"]        = tenantContext.CurrentTenantId.ToString(),
                ["user_id"]          = user.Id.ToString()
            },
            IdempotencyKey: $"user-activation-{user.Id}",
            TenantId:       tenantContext.CurrentTenantId), ct);

        // AuditBehavior writes the AuditEntry automatically (ICommand marker).
        return Result.Ok(new CreateUserResult(user.Id));
    }
}
