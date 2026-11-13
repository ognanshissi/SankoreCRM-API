using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Administration.Domain;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Sankore.Modules.Administration.Infrastructure;
using Sankore.Modules.Administration.Infrastructure.Branding;
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

    /// <summary>Sans <c>Administration:Avatars:EncryptionKey</c>, aucun magasin : le dépôt est refusé plutôt qu'écrit en clair.</summary>
    public const string Unavailable = "AVATARS_UNAVAILABLE";

    private static async Task<IResult> UploadAsync(
        IFormFile file, AdministrationDbContext db, ICurrentUser currentUser, IServiceProvider services,
        ILoggerFactory loggers, CancellationToken ct)
    {
        // Refusé avant lecture : inutile de charger 50 Mo en mémoire pour dire qu'il en fallait 2.
        if (file.Length > UserAvatar.MaxSizeBytes)
            return Results.Problem("La photo ne peut pas dépasser 2 Mo.", statusCode: StatusCodes.Status400BadRequest);

        if (services.GetService<IUserAvatarStore>() is not { } store)
            return Results.Problem(
                "Le dépôt de photo n'est pas disponible pour le moment.",
                statusCode: StatusCodes.Status503ServiceUnavailable,
                extensions: new Dictionary<string, object?> { ["error"] = Unavailable });

        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == currentUser.Id, ct);
        if (user is null)
            return Results.NotFound();

        using var buffer = new MemoryStream();
        await file.CopyToAsync(buffer, ct);
        var content = buffer.ToArray();

        string contentType;
        try
        {
            contentType = UserAvatar.ContentTypeOf(content);
        }
        catch (DomainException ex)
        {
            return Results.Problem(ex.Message, statusCode: StatusCodes.Status400BadRequest);
        }

        var storageRef = await store.StoreAsync(user.TenantId, content, ct);

        var avatar = await db.UserAvatars.AsTracking().FirstOrDefaultAsync(a => a.UserId == user.Id, ct);
        var previousRef = avatar?.StorageRef;
        if (avatar is null)
            db.UserAvatars.Add(UserAvatar.Create(user.TenantId, user.Id, storageRef, contentType));
        else
            avatar.Replace(storageRef, contentType);

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch
        {
            await DeleteQuietlyAsync(store, user.TenantId, storageRef, loggers);
            throw;
        }

        if (previousRef is not null)
            await DeleteQuietlyAsync(store, user.TenantId, previousRef, loggers);

        return Results.NoContent();
    }

    private static async Task<IResult> DeleteAsync(
        AdministrationDbContext db, ICurrentUser currentUser, IServiceProvider services, ILoggerFactory loggers, CancellationToken ct)
    {
        var avatar = await db.UserAvatars.AsTracking().FirstOrDefaultAsync(a => a.UserId == currentUser.Id, ct);
        if (avatar is not null)
        {
            var (tenantId, storageRef) = (avatar.TenantId, avatar.StorageRef);
            db.UserAvatars.Remove(avatar);
            await db.SaveChangesAsync(ct);

            if (storageRef is not null && services.GetService<IUserAvatarStore>() is { } store)
                await DeleteQuietlyAsync(store, tenantId, storageRef, loggers);
        }
        return Results.NoContent();
    }

    /// <summary>Un objet qui reste n'est qu'un orphelin chiffré dans le bucket ; il ne doit pas faire échouer la réponse.</summary>
    private static async Task DeleteQuietlyAsync(IUserAvatarStore store, Guid tenantId, string storageRef, ILoggerFactory loggers)
    {
        try
        {
            await store.DeleteAsync(tenantId, storageRef, CancellationToken.None);
        }
        catch (Exception ex)
        {
            loggers.CreateLogger(typeof(MyAvatarEndpoints)).LogWarning(ex, "Could not delete profile photo object {StorageRef}", storageRef);
        }
    }
}
