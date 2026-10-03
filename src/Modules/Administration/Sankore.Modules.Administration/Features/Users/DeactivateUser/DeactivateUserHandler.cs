using MediatR;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Sankore.Modules.Administration.Domain;
using Sankore.Modules.Administration.Infrastructure;
using Sankore.Shared.Infrastructure.Messaging;
using Sankore.Shared.Kernel;

namespace Sankore.Modules.Administration.Features.Users.DeactivateUser;

internal sealed class DeactivateUserHandler(
    AdministrationDbContext db,
    UserManager<AppUser> userManager,
    [FromKeyedServices(nameof(AdministrationDbContext))] IEventPublisher publisher
) : IRequestHandler<DeactivateUserCommand, Result>
{
    public async Task<Result> Handle(DeactivateUserCommand request, CancellationToken ct)
    {
        // 1. Load with tracking so EF picks up all mutations in one SaveChangesAsync.
        //    Filtered include: only active UserRole records — those are the ones we revoke.
        //    Tenant query filter is applied automatically on both User and UserRole.
        var user = await db.Users
            .AsTracking()
            .Include(u => u.UserRoles.Where(r => r.IsActive))
            .Where(u => u.Id == request.UserId)
            .SingleOrDefaultAsync(ct);

        if (user is null)
            return Result.Fail($"User {request.UserId} not found.");

        // 2. Domain guard — idempotency check (Gherkin S3: double-deactivation rejected).
        if (user.Status == UserStatus.Disabled)
            return Result.Fail("User is already disabled.");

        // 3. Domain transition — Status → Disabled, DeactivatedAt = now.
        var deactivatedEvent = user.Deactivate();

        // 4. Revoke all active role assignments — in BOTH stores.
        //
        //    db.UserRoles is only the tenant-scoped audit mirror; the roles that actually
        //    grant access are Identity's own, which is what login reads through
        //    UserManager.GetRolesAsync (see AdministrationDbContext.UserRoles and the note on
        //    BulkAssignRoleHandler). Revoking the mirror alone used to leave the Identity rows
        //    intact, so this step's claim to prevent role-based access was not true: the
        //    Status guard in LoginHandler blocked the login, but the instant the account was
        //    reactivated the old roles were back in the JWT while every read of the mirror
        //    reported none. An administrator looking at GET users/{id} saw a user with no
        //    roles who in fact still had all of them.
        //
        //    Fail closed: the grants end here, and reactivation deliberately does NOT restore
        //    them — ReactivateUserHandler says why.
        foreach (var userRole in user.UserRoles)
            userRole.Revoke();

        var identityRoles = await userManager.GetRolesAsync(user);
        if (identityRoles.Count > 0)
        {
            var removal = await userManager.RemoveFromRolesAsync(user, identityRoles);
            if (!removal.Succeeded)
            {
                // Before SaveChangesAsync and before the event: refusing here leaves the user
                // active rather than half-deactivated with roles that still grant access.
                return Result.Fail(
                    "Failed to revoke the user's roles: "
                    + string.Join("; ", removal.Errors.Select(e => e.Description)));
            }
        }

        // 5. Persist: user state + role revocations committed atomically by TransactionBehavior.
        await db.SaveChangesAsync(ct);

        // 6. Publish integration event via Outbox.
        //    The Leads module subscribes to reassign this agent's active leads.
        await publisher.PublishAsync(deactivatedEvent, ct);

        // AuditBehavior writes AuditEntry automatically (ICommand marker).
        return Result.Ok();
    }
}
