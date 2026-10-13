using System.Security.Claims;
using FluentValidation;
using MediatR;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Administration.Domain;
using Sankore.Modules.Administration.Infrastructure;
using Sankore.Modules.Administration.Infrastructure.JwtToken;
using Sankore.Modules.Administration.Infrastructure.Sessions;
using Sankore.Shared.Infrastructure.Auth;
using Sankore.Shared.Infrastructure.Behaviors;
using Sankore.Shared.Kernel;

namespace Sankore.Modules.Administration.Features.Users.Impersonation;

public sealed record ImpersonateUserRequest(string Reason);

/// <param name="AccessToken">Jeton de l'utilisateur usurpé, sans jeton de rafraîchissement.</param>
public sealed record ImpersonationStartedDto(
    string AccessToken, DateTimeOffset ExpiresAt, Guid UserId, string UserFullName);

/// <summary>
/// Agir sous l'identité d'un utilisateur, pour voir ce qu'il voit.
///
/// <para>
/// Les garde-fous, tous côté serveur :
/// <list type="bullet">
/// <item>une permission à part, <c>user:impersonate</c> ;</item>
/// <item>un motif obligatoire, écrit dans l'audit avec la commande ;</item>
/// <item>jamais soi-même, jamais un compte système ou super-utilisateur, jamais un compte inactif ;</item>
/// <item>jamais depuis une usurpation (pas d'enchaînement) ;</item>
/// <item>un jeton de 30 minutes sans rafraîchissement, porteur de l'identité de l'administrateur,
///   adossé à une session révocable : chaque action est auditée au nom des deux, et la sortie
///   coupe la session sur-le-champ.</item>
/// </list>
/// </para>
/// </summary>
public sealed record ImpersonateUserCommand(Guid TargetUserId, string Reason, string? IpAddress, string? UserAgent)
    : IRequest<Result<ImpersonationStartedDto>>, ICommand, IResourceCommand
{
    public string ResourceType => "User";
    public string? ResourceId => TargetUserId.ToString();
}

internal sealed class ImpersonateUserValidator : AbstractValidator<ImpersonateUserCommand>
{
    public ImpersonateUserValidator()
        => RuleFor(x => x.Reason).NotEmpty().MinimumLength(5).MaximumLength(500)
            .WithMessage("Indiquez le motif de l'usurpation (5 caractères au moins).");
}

internal sealed class ImpersonateUserHandler(
    AdministrationDbContext db,
    UserManager<AppUser> userManager,
    ICurrentUser currentUser,
    IJwtTokenService jwt)
    : IRequestHandler<ImpersonateUserCommand, Result<ImpersonationStartedDto>>
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(30);

    public async Task<Result<ImpersonationStartedDto>> Handle(ImpersonateUserCommand command, CancellationToken ct)
    {
        if (currentUser.ImpersonatorId is not null)
            return Result.Fail<ImpersonationStartedDto>("Revenez d'abord à votre propre compte.");
        if (command.TargetUserId == currentUser.Id)
            return Result.Fail<ImpersonationStartedDto>("Vous ne pouvez pas vous usurper vous-même.");

        var actor = await db.Users.FirstOrDefaultAsync(u => u.Id == currentUser.Id, ct);
        var target = await db.Users.FirstOrDefaultAsync(u => u.Id == command.TargetUserId, ct);
        if (actor is null || target is null)
            return Result.Fail<ImpersonationStartedDto>("USER_NOT_FOUND");

        if (target.IsSuperUser || target.AccountType != UserAccountType.Standard)
            return Result.Fail<ImpersonationStartedDto>("Les comptes système et super-utilisateurs ne peuvent pas être usurpés.");
        if (target.Status != UserStatus.Active)
            return Result.Fail<ImpersonationStartedDto>("Seul un compte actif peut être usurpé.");

        var roles = await userManager.GetRolesAsync(target);
        var roleIds = await db.Roles.Where(r => roles.Contains(r.Name!)).Select(r => r.Id).ToListAsync(ct);
        var permissions = await db.RolePermissions
            .Where(rp => roleIds.Contains(rp.RoleId))
            .Select(rp => rp.Permission.Code)
            .Union(db.PermissionAttributions.Where(pa => pa.UserId == target.Id && pa.IsActive).Select(pa => pa.PermissionCode))
            .Distinct()
            .ToListAsync(ct);

        var session = UserSession.OpenImpersonation(
            target.TenantId, target.Id, actor.Id, command.IpAddress, UserAgentParser.Parse(command.UserAgent));
        db.UserSessions.Add(session);
        await db.SaveChangesAsync(ct);

        var token = jwt.CreateToken(
            target, roles, permissions, target.PreferredLanguage ?? LanguageCode.Default, session.Id,
            new ImpersonationGrant(actor.Id, actor.FullName, Lifetime));

        return Result.Ok(new ImpersonationStartedDto(token.Token, token.ExpiresAt, target.Id, target.FullName));
    }
}

/// <summary>Met fin à l'usurpation en cours : la session est fermée, le jeton cesse de valoir.</summary>
public sealed record EndImpersonationCommand(Guid? SessionId) : IRequest<Result>, ICommand;

internal sealed class EndImpersonationHandler(AdministrationDbContext db, ICurrentUser currentUser)
    : IRequestHandler<EndImpersonationCommand, Result>
{
    public async Task<Result> Handle(EndImpersonationCommand command, CancellationToken ct)
    {
        if (currentUser.ImpersonatorId is null || command.SessionId is not { } sessionId)
            return Result.Fail("Aucune usurpation en cours.");

        await SessionTerminator.EndAsync(db, currentUser.Id, SessionEndReason.ImpersonationEnded, onlySessionId: sessionId, ct: ct);
        await db.SaveChangesAsync(ct);
        return Result.Ok();
    }
}

internal static class ImpersonationEndpoints
{
    internal static IEndpointRouteBuilder MapImpersonation(this IEndpointRouteBuilder app)
    {
        app.MapPost("{userId:guid}/impersonate", async (
                Guid userId, ImpersonateUserRequest req, HttpContext http, ISender sender, CancellationToken ct) =>
            {
                var result = await sender.Send(new ImpersonateUserCommand(
                    userId, req.Reason?.Trim() ?? string.Empty,
                    http.Connection.RemoteIpAddress?.ToString(), http.Request.Headers.UserAgent.ToString()), ct);
                return result.IsSuccess
                    ? Results.Ok(result.Value)
                    : result.Error == "USER_NOT_FOUND"
                        ? Results.NotFound()
                        : Results.Problem(result.Error, statusCode: StatusCodes.Status400BadRequest);
            })
            .WithName("ImpersonateUser")
            .WithSummary("Ouvre une session de 30 minutes sous l'identité d'un utilisateur actif. Motif obligatoire ; chaque action est auditée au nom de l'administrateur et de l'utilisateur. Requires permission: user:impersonate.")
            .WithTags("Users")
            .RequireAuthorization(Permissions.CanImpersonateUser.Code)
            .DisallowImpersonation()
            .Produces<ImpersonationStartedDto>()
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status404NotFound);

        app.MapPost("me/impersonation/end", async (ClaimsPrincipal user, ISender sender, CancellationToken ct) =>
            {
                var result = await sender.Send(new EndImpersonationCommand(SessionClaim.Read(user)), ct);
                return result.IsSuccess
                    ? Results.NoContent()
                    : Results.Problem(result.Error, statusCode: StatusCodes.Status400BadRequest);
            })
            .WithName("EndImpersonation")
            .WithSummary("Met fin à l'usurpation d'identité en cours ; la session et son jeton cessent de valoir.")
            .WithTags("Users")
            .RequireAuthorization()
            .Produces(StatusCodes.Status204NoContent)
            .Produces(StatusCodes.Status400BadRequest);

        return app;
    }
}
