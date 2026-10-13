using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Sankore.Modules.Administration.Domain;
using Sankore.Modules.Administration.Infrastructure;
using Sankore.Modules.Administration.Infrastructure.Identity;
using Sankore.Shared.Kernel;

namespace Sankore.Modules.Administration.Features.Users.GetUser;

internal sealed class GetUserHandler(
    AdministrationDbContext db,
    IOptions<IdentityTokenOptions>? tokenOptions = null
) : IRequestHandler<GetUserQuery, Result<UserDto>>
{
    public async Task<Result<UserDto>> Handle(GetUserQuery request, CancellationToken ct)
    {
        var user = await db.Users
            .Include(u => u.Agency)
            .Where(u => u.Id == request.UserId)
            .SingleOrDefaultAsync(ct);

        if (user is null)
            return Result.Fail<UserDto>($"User {request.UserId} not found.");

        var roleRows = await db.UserRoles
            .Where(ur => ur.UserId == user.Id && ur.IsActive)
            .OrderBy(ur => ur.AssignedAt)
            .Join(db.Roles, ur => ur.RoleId, r => r.Id, (ur, r) => new { r.Name, r.Label })
            .ToListAsync(ct);
        var roles = roleRows.Select(r => r.Name!).ToList();

        var now = DateTimeOffset.UtcNow;
        var attributions = await db.PermissionAttributions
            .CountAsync(a => a.UserId == user.Id && a.IsActive && a.EndDate > now, ct);
        var directReports = await db.Users.CountAsync(u => u.ReportsToUserId == user.Id, ct);

        var profile = await db.UserProfiles
            .Where(p => p.UserId == user.Id)
            .Select(p => new { Phone = p.WorkNumber != null ? p.WorkNumber.Contact : null })
            .FirstOrDefaultAsync(ct);
        var avatar = await db.UserAvatars.FirstOrDefaultAsync(a => a.UserId == user.Id, ct);

        var createdByName = user.CreatedByUserId is { } creatorId
            ? await db.Users.Where(u => u.Id == creatorId).Select(u => u.FullName).FirstOrDefaultAsync(ct)
            : null;

        // Le responsable tel que la ligne hiérarchique l'affiche : nom, premier rôle, agence.
        var manager = user.ReportsToUserId is { } managerId
            ? await db.Users
                .Where(u => u.Id == managerId)
                .Select(u => new
                {
                    u.FullName,
                    AgencyName = u.Agency != null ? u.Agency.Name : null,
                    RoleLabel = db.UserRoles
                        .Where(ur => ur.UserId == u.Id && ur.IsActive)
                        .OrderBy(ur => ur.AssignedAt)
                        .Select(ur => ur.Role!.Label)
                        .FirstOrDefault(),
                })
                .FirstOrDefaultAsync(ct)
            : null;

        var pending = user.Status == UserStatus.PendingActivation;
        var lifespan = tokenOptions?.Value.ActivationTokenLifespan ?? IdentityTokenOptions.DefaultActivationLifespan;
        // Avant la colonne, l'invitation partait à la création : c'est la meilleure date connue.
        var invitationSentAt = pending ? user.InvitationSentAt ?? user.CreatedAt : null;

        return Result.Ok(new UserDto(
            user.Id,
            user.FullName,
            user.Email!,
            user.Status.ToString(),
            user.AgencyId,
            user.Agency?.Name,
            user.MfaEnabled,
            user.PasswordExpiresAt,
            user.LastLoginAt,
            user.DeactivatedAt,
            user.SpokenLanguages,
            user.Specialties,
            user.IsAvailable,
            user.EnableNotifications,
            user.AccountType.ToString(),
            roles,
            user.ReportsToUserId,
            manager?.FullName,
            user.EmployeeNumber,
            FirstName: user.FirstName,
            LastName: user.LastName,
            WorkPhone: string.IsNullOrWhiteSpace(profile?.Phone) ? null : profile!.Phone,
            AvatarDataUrl: avatar?.ToDataUrl(),
            CreatedAt: user.CreatedAt,
            CreatedByName: createdByName,
            InvitationSentAt: invitationSentAt,
            InvitationExpiresAt: invitationSentAt + lifespan,
            RoleLabels: roleRows.Select(r => string.IsNullOrWhiteSpace(r.Label) ? r.Name! : r.Label).ToList(),
            ActiveAttributionsCount: attributions,
            DirectReportsCount: directReports,
            ReportsToRoleLabel: string.IsNullOrWhiteSpace(manager?.RoleLabel) ? null : manager!.RoleLabel,
            ReportsToAgencyName: manager?.AgencyName));
    }
}
