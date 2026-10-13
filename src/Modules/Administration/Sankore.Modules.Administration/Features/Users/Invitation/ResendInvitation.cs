using MediatR;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Administration.Domain;
using Sankore.Modules.Administration.Infrastructure;
using Sankore.Shared.Infrastructure.Behaviors;
using Sankore.Shared.Kernel;

namespace Sankore.Modules.Administration.Features.Users.Invitation;

public sealed record InvitationSentDto(DateTimeOffset SentAt, DateTimeOffset ExpiresAt);

/// <summary>
/// Renvoie le lien d'activation d'un compte en attente. Le nouveau lien invalide les précédents :
/// un lien égaré dans une boîte partagée ne reste pas utilisable à côté du bon.
/// </summary>
public sealed record ResendInvitationCommand(Guid UserId) : IRequest<Result<InvitationSentDto>>, ICommand, IResourceCommand
{
    public string ResourceType => "User";
    public string? ResourceId => UserId.ToString();
}

internal sealed class ResendInvitationHandler(AdministrationDbContext db, ActivationInvitationSender invitations)
    : IRequestHandler<ResendInvitationCommand, Result<InvitationSentDto>>
{
    public async Task<Result<InvitationSentDto>> Handle(ResendInvitationCommand command, CancellationToken ct)
    {
        var user = await db.Users.AsTracking().FirstOrDefaultAsync(u => u.Id == command.UserId, ct);
        if (user is null)
            return Result.Fail<InvitationSentDto>("USER_NOT_FOUND");
        if (user.Status != UserStatus.PendingActivation)
            return Result.Fail<InvitationSentDto>("Ce compte est déjà activé : il n'y a plus d'invitation à renvoyer.");

        var locale = user.PreferredLanguage
            ?? await db.UserProfiles.Where(p => p.UserId == user.Id).Select(p => p.DefaultLanguage).FirstOrDefaultAsync(ct)
            ?? LanguageCode.Default;

        var sentAt = await invitations.SendAsync(user, locale, invalidatePrevious: true, ct);
        await db.SaveChangesAsync(ct);
        return Result.Ok(new InvitationSentDto(sentAt, sentAt + invitations.Lifespan));
    }
}

internal static class ResendInvitationEndpoint
{
    internal static IEndpointRouteBuilder MapResendInvitation(this IEndpointRouteBuilder app)
    {
        app.MapPost("{userId:guid}/invitation", async (Guid userId, ISender sender, CancellationToken ct) =>
            {
                var result = await sender.Send(new ResendInvitationCommand(userId), ct);
                return result.IsSuccess
                    ? Results.Ok(result.Value)
                    : result.Error == "USER_NOT_FOUND"
                        ? Results.NotFound()
                        : Results.Problem(result.Error, statusCode: StatusCodes.Status400BadRequest);
            })
            .WithName("ResendUserInvitation")
            .WithSummary("Renvoie le lien d'activation d'un compte en attente ; les liens précédents cessent de fonctionner. Requires permission: user:update.")
            .WithTags("Users")
            .RequireAuthorization(Permissions.CanUpdateUser.Code)
            .Produces<InvitationSentDto>()
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status404NotFound);

        return app;
    }
}
