using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;

namespace Sankore.Modules.Administration.Features.Authentication.VerifyToken;

public static class VerifyTokenEndpoint
{
    public static IEndpointRouteBuilder MapVerifyToken(this IEndpointRouteBuilder app)
    {
        app.MapGet("/auth/verify", Handle)
            .WithTags("Auth")
            .WithName("VerifyToken")
            .Produces<VerifyTokenResponse>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status401Unauthorized)
            .RequireAuthorization();
        return app;
    }

    public static IResult Handle(ClaimsPrincipal user)
    {
        var userId = Guid.Parse(user.FindFirstValue(ClaimTypes.NameIdentifier)!);
        var email = user.FindFirstValue(ClaimTypes.Email)!;
        var name = user.FindFirstValue("name")!;
        var roles = user.FindAll(ClaimTypes.Role).Select(c => c.Value).ToArray();
        var permissions = user.FindAll("permission").Select(c => c.Value).ToArray();

        var expClaim = user.FindFirstValue(JwtRegisteredClaimNames.Exp);
        DateTimeOffset? expiresAt = expClaim is not null
            ? DateTimeOffset.FromUnixTimeSeconds(long.Parse(expClaim))
            : null;

        return Results.Ok(new VerifyTokenResponse(userId, email, name, roles, permissions, expiresAt));
    }
}

public sealed record VerifyTokenResponse(
    Guid UserId,
    string Email,
    string Name,
    string[] Roles,
    string[] Permissions,
    DateTimeOffset? ExpiresAt);
