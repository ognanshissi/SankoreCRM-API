using MediatR;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Administration.Domain;
using Sankore.Modules.Administration.Infrastructure;
using Sankore.Shared.Infrastructure.Auth;
using Sankore.Shared.Kernel;

namespace Sankore.Modules.Administration.Features.Users.MyProfile;

public sealed record GetMyProfileQuery : IRequest<Result<MyProfileDto>>;

internal sealed class GetMyProfileHandler(AdministrationDbContext db, ICurrentUser currentUser)
    : IRequestHandler<GetMyProfileQuery, Result<MyProfileDto>>
{
    public async Task<Result<MyProfileDto>> Handle(GetMyProfileQuery request, CancellationToken ct)
    {
        var user = await db.Users
            .Include(u => u.Agency)
            .FirstOrDefaultAsync(u => u.Id == currentUser.Id, ct);
        if (user is null)
            return Result.Fail<MyProfileDto>("USER_NOT_FOUND");

        var profile = await db.UserProfiles.FirstOrDefaultAsync(p => p.UserId == user.Id, ct);
        var avatar = await db.UserAvatars.FirstOrDefaultAsync(a => a.UserId == user.Id, ct);

        // Libellé lu ici plutôt que par GET roles : un agent n'a pas forcément le droit de
        // lister les rôles, mais doit pouvoir lire le sien.
        var roleLabel = await db.UserRoles
            .Where(ur => ur.UserId == user.Id && ur.IsActive)
            .OrderBy(ur => ur.AssignedAt)
            .Select(ur => ur.Role!.Label)
            .FirstOrDefaultAsync(ct);

        string? managerName = null;
        if (user.ReportsToUserId is { } managerId)
            managerName = await db.Users.Where(u => u.Id == managerId).Select(u => u.FullName).FirstOrDefaultAsync(ct);

        return Result.Ok(new MyProfileDto(
            user.FirstName,
            user.LastName,
            user.Email!,
            user.EmployeeNumber,
            profile?.WorkNumber?.Contact is { Length: > 0 } phone ? phone : null,
            profile?.WhatsAppNumber,
            profile?.Signature,
            user.PreferredLanguage ?? profile?.DefaultLanguage ?? LanguageCode.Default,
            profile?.TimeZone ?? UserProfile.DefaultTimeZone,
            (profile?.AmountFormat ?? AmountFormat.SpaceGrouped).ToString(),
            avatar?.ToDataUrl(),
            string.IsNullOrWhiteSpace(roleLabel) ? null : roleLabel,
            user.Agency?.Name,
            managerName,
            user.CreatedAt,
            profile?.UpdatedAt));
    }
}

internal static class GetMyProfileEndpoint
{
    internal static IEndpointRouteBuilder MapGetMyProfile(this IEndpointRouteBuilder app)
    {
        app.MapGet("me/profile", async (ISender sender, CancellationToken ct) =>
            {
                var result = await sender.Send(new GetMyProfileQuery(), ct);
                return result.IsSuccess ? Results.Ok(result.Value) : Results.NotFound();
            })
            .WithName("GetMyProfile")
            .WithSummary("Profil de l'utilisateur connecté : identité, coordonnées, signature, langue, fuseau, format des montants et rattachement.")
            .WithTags("Users")
            .RequireAuthorization()
            .Produces<MyProfileDto>()
            .Produces(StatusCodes.Status404NotFound);

        return app;
    }
}
