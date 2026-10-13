using System.Security.Claims;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace Sankore.Shared.Infrastructure.Auth;

/// <summary>
/// Les claims d'un jeton d'usurpation. Le jeton est celui de l'utilisateur usurpé — mêmes rôles,
/// mêmes permissions, ce qui est tout l'intérêt — plus l'identité de l'administrateur, qui ne
/// s'efface jamais : l'audit l'écrit à côté de chaque action.
/// </summary>
public static class ImpersonationClaims
{
    /// <summary>Identifiant de l'administrateur (« actor » au sens de RFC 8693).</summary>
    public const string ActorId = "act_sub";

    public const string ActorName = "act_name";

    public static Guid? ReadImpersonatorId(ClaimsPrincipal? principal)
        => Guid.TryParse(principal?.FindFirst(ActorId)?.Value, out var id) ? id : null;

    public static bool IsImpersonating(ClaimsPrincipal? principal) => ReadImpersonatorId(principal) is not null;

    /// <summary>
    /// Refuse l'endpoint sous usurpation. Pour ce qui touche aux secrets du compte usurpé (mot de
    /// passe, sessions, appareils) et pour l'usurpation elle-même : regarder avec les yeux de
    /// quelqu'un n'autorise pas à changer ses clés, ni à enchaîner une deuxième identité.
    /// </summary>
    public static TBuilder DisallowImpersonation<TBuilder>(this TBuilder builder) where TBuilder : IEndpointConventionBuilder
        => builder.AddEndpointFilter(async (context, next) =>
            IsImpersonating(context.HttpContext.User)
                ? Results.Problem(
                    "Action impossible pendant une usurpation d'identité. Revenez à votre compte.",
                    statusCode: StatusCodes.Status403Forbidden)
                : await next(context));
}
