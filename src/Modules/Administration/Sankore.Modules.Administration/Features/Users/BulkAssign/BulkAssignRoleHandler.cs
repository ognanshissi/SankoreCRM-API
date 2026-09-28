using MediatR;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Administration.Domain;
using Sankore.Modules.Administration.Infrastructure;
using Sankore.Shared.Infrastructure.Auth;
using Sankore.Shared.Kernel;

namespace Sankore.Modules.Administration.Features.Users.BulkAssign;

internal sealed class BulkAssignRoleHandler(
    AdministrationDbContext db,
    UserManager<AppUser> userManager,
    RoleManager<AppRole> roleManager,
    ICurrentUser currentUser,
    BulkAssignNotifier notifier
) : IRequestHandler<BulkAssignRoleCommand, Result<BulkAssignResult>>
{
    public async Task<Result<BulkAssignResult>> Handle(
        BulkAssignRoleCommand request, CancellationToken ct)
    {
        var role = await roleManager.FindByIdAsync(request.RoleId.ToString());
        if (role is null)
            return Result.Fail<BulkAssignResult>(BulkAssignErrors.RoleNotFound);
        if (!role.IsAssignable)
            return Result.Fail<BulkAssignResult>(BulkAssignErrors.RoleNotAssignable);

        var userIds = request.UserIds.Distinct().ToList();

        var users = await db.Users
            .AsTracking()
            .Where(u => userIds.Contains(u.Id))
            .ToListAsync(ct);

        var alreadyHolding = await db.UserRoles
            .Where(ur => ur.RoleId == role.Id && ur.IsActive && userIds.Contains(ur.UserId))
            .Select(ur => ur.UserId)
            .ToListAsync(ct);
        var holders = alreadyHolding.ToHashSet();

        var outcomes = new List<BulkUserOutcome>(userIds.Count);
        var granted = new List<AppUser>();

        foreach (var userId in userIds)
        {
            var user = users.Find(u => u.Id == userId);

            if (user is null)
            {
                outcomes.Add(new(userId, false, BulkAssignReasons.UserNotFound));
                continue;
            }

            if (user.AccountType == UserAccountType.System)
            {
                outcomes.Add(new(userId, false, BulkAssignReasons.SystemAccountImmutable));
                continue;
            }

            if (holders.Contains(userId))
            {
                outcomes.Add(new(userId, false, BulkAssignReasons.AlreadyHasRole));
                continue;
            }

            // Identity owns the roles login reads; db.UserRoles is the audit mirror, exactly as
            // AssignRoleHandler does it for a single user.
            var identityResult = await userManager.AddToRoleAsync(user, role.Name!);
            if (!identityResult.Succeeded)
            {
                outcomes.Add(new(userId, false,
                    string.Join("; ", identityResult.Errors.Select(e => e.Description))));
                continue;
            }

            // Attributed to the operator, not to the system sentinel: this is a deliberate act,
            // so a later agency handover must never revoke it automatically.
            await db.UserRoles.AddAsync(
                UserRole.Assign(currentUser.TenantId, user.Id, role.Id, currentUser.Id), ct);

            outcomes.Add(new(userId, true, null));
            granted.Add(user);
        }

        await db.SaveChangesAsync(ct);

        // Only the users who actually gained the role are told, and only once it is committed.
        var changedAt = DateTimeOffset.UtcNow;
        foreach (var user in granted)
            await notifier.NotifyRoleGrantedAsync(currentUser.TenantId, role, user, changedAt, ct);

        return Result.Ok(BulkAssignAgencyHandler.Report(userIds.Count, outcomes));
    }
}
