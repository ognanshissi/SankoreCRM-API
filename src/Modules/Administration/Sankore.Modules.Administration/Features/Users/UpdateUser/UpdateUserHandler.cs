using MediatR;
using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Administration.Domain;
using Sankore.Modules.Administration.Features.Users.Invitation;
using Sankore.Modules.Administration.Infrastructure;
using Sankore.Shared.Kernel;

namespace Sankore.Modules.Administration.Features.Users.UpdateUser;

internal sealed class UpdateUserHandler(
    AdministrationDbContext db,
    ActivationInvitationSender invitations
) : IRequestHandler<UpdateUserCommand, Result>
{
    public async Task<Result> Handle(UpdateUserCommand request, CancellationToken ct)
    {
        var user = await db.Users
            .AsTracking()
            .Where(u => u.Id == request.UserId)
            .SingleOrDefaultAsync(ct);

        if (user is null)
            return Result.Fail($"User {request.UserId} not found.");

        if (request.AgencyId.HasValue)
        {
            var agencyExists = await db.Agencies
                .AnyAsync(a => a.Id == request.AgencyId.Value, ct);
            if (!agencyExists)
                return Result.Fail($"Agency {request.AgencyId} not found.");
        }

        var newEmail = request.Email?.Trim();
        var emailChanged = newEmail is not null
            && !string.Equals(newEmail, user.Email, StringComparison.OrdinalIgnoreCase);
        if (emailChanged)
        {
            var normalized = newEmail!.ToUpperInvariant();
            if (await db.Users.IgnoreQueryFilters().AnyAsync(
                    u => u.TenantId == user.TenantId && u.Id != user.Id && u.NormalizedEmail == normalized, ct))
                return Result.Fail("Cette adresse est déjà utilisée par un autre utilisateur.");
        }

        if (!string.IsNullOrWhiteSpace(request.EmployeeNumber))
        {
            var number = request.EmployeeNumber.Trim();
            if (await db.Users.IgnoreQueryFilters().AnyAsync(
                    u => u.TenantId == user.TenantId && u.Id != user.Id && u.EmployeeNumber == number, ct))
                return Result.Fail("Ce matricule est déjà attribué.");
        }

        try
        {
            user.UpdateDetails(
                request.FullName,
                request.AgencyId,
                request.SpokenLanguages,
                request.Specialties,
                request.EnableNotifications);
            if (request.FirstName is not null || request.LastName is not null)
                user.RenameSelf(request.FirstName ?? user.FirstName, request.LastName ?? user.LastName);
            if (request.EmployeeNumber is not null)
                user.SetEmployeeNumber(request.EmployeeNumber);
            if (emailChanged)
                user.ChangeEmailBeforeActivation(newEmail!);
        }
        catch (DomainException ex)
        {
            return Result.Fail(ex.Message);
        }

        if (request.WorkPhone is not null)
        {
            var profile = await db.UserProfiles.AsTracking().FirstOrDefaultAsync(p => p.UserId == user.Id, ct);
            if (profile is null)
            {
                profile = UserProfile.Create(user.TenantId, user.Id);
                db.UserProfiles.Add(profile);
            }
            profile.SetWorkPhone(request.WorkPhone);
        }

        await db.SaveChangesAsync(ct);

        // Le lien part à la nouvelle adresse et invalide celui envoyé à l'ancienne : une coquille
        // corrigée ne doit pas laisser un lien valide dans une boîte qui n'est pas la bonne.
        if (emailChanged)
        {
            var locale = await db.UserProfiles.Where(p => p.UserId == user.Id).Select(p => p.DefaultLanguage).FirstOrDefaultAsync(ct);
            await invitations.SendAsync(user, user.PreferredLanguage ?? locale ?? LanguageCode.Default, invalidatePrevious: true, ct);
            await db.SaveChangesAsync(ct);
        }

        return Result.Ok();
    }
}
