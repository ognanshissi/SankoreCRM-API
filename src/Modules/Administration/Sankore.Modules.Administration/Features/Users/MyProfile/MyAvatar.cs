using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Administration.Domain;
using Sankore.Modules.Administration.Infrastructure;
using Sankore.Shared.Infrastructure.Auth;
using Sankore.Shared.Kernel;

namespace Sankore.Modules.Administration.Features.Users.MyProfile;

/// <summary>Photo de l'utilisateur connecté : remplacer ou retirer. Toujours la sienne.</summary>
internal static class MyAvatarEndpoints
{
    internal static IEndpointRouteBuilder MapMyAvatar(this IEndpointRouteBuilder app)
    {
        app.MapPut("me/avatar", UploadAsync)
            .WithName("UploadMyAvatar")
            .WithSummary("Remplace la photo de profil de l'utilisateur connecté (JPG ou PNG, 2 Mo maximum).")
            .WithTags("Users")
            .RequireAuthorization()
            .Accepts<IFormFile>("multipart/form-data")
            .Produces(StatusCodes.Status204NoContent)
            .Produces(StatusCodes.Status400BadRequest)
            .DisableAntiforgery();

        app.MapDelete("me/avatar", DeleteAsync)
            .WithName("DeleteMyAvatar")
            .WithSummary("Retire la photo de profil de l'utilisateur connecté.")
            .WithTags("Users")
            .RequireAuthorization()
            .Produces(StatusCodes.Status204NoContent);

        return app;
    }

    private static async Task<IResult> UploadAsync(
        IFormFile file, AdministrationDbContext db, ICurrentUser currentUser, CancellationToken ct)
    {
        // Refusé avant lecture : inutile de charger 50 Mo en mémoire pour dire qu'il en fallait 2.
        if (file.Length > UserAvatar.MaxSizeBytes)
            return Results.Problem("La photo ne peut pas dépasser 2 Mo.", statusCode: StatusCodes.Status400BadRequest);

        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == currentUser.Id, ct);
        if (user is null)
            return Results.NotFound();

        using var buffer = new MemoryStream();
        await file.CopyToAsync(buffer, ct);
        var content = buffer.ToArray();

        var avatar = await db.UserAvatars.AsTracking().FirstOrDefaultAsync(a => a.UserId == user.Id, ct);
        try
        {
            if (avatar is null)
                db.UserAvatars.Add(UserAvatar.Create(user.TenantId, user.Id, content));
            else
                avatar.Replace(content);
        }
        catch (DomainException ex)
        {
            return Results.Problem(ex.Message, statusCode: StatusCodes.Status400BadRequest);
        }

        await db.SaveChangesAsync(ct);
        return Results.NoContent();
    }

    private static async Task<IResult> DeleteAsync(
        AdministrationDbContext db, ICurrentUser currentUser, CancellationToken ct)
    {
        var avatar = await db.UserAvatars.AsTracking().FirstOrDefaultAsync(a => a.UserId == currentUser.Id, ct);
        if (avatar is not null)
        {
            db.UserAvatars.Remove(avatar);
            await db.SaveChangesAsync(ct);
        }
        return Results.NoContent();
    }
}
