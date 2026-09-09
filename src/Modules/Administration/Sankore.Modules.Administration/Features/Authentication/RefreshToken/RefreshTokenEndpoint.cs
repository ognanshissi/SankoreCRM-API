using MediatR;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Sankore.Modules.Administration.Features.Authentication.Login;
using Sankore.Shared.Infrastructure.Extensions;

namespace Sankore.Modules.Administration.Features.Authentication.RefreshToken;

public static class RefreshTokenEndpoint
{
    public static IEndpointRouteBuilder MapRefreshToken(this IEndpointRouteBuilder app)
    {
        app.MapPost("/auth/refresh", Handle)
            .WithTags("Auth")
            .WithName("RefreshToken")
            .Produces<LoginResult>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status401Unauthorized)
            .WithTenantHeader()
            .AllowAnonymous()
            .WithOpenApi()
            .RequireRateLimiting("auth");
        return app;
    }

    public static async Task<IResult> Handle(RefreshTokenRequest req, ISender sender, CancellationToken ct)
    {
        var result = await sender.Send(new RefreshTokenCommand(req.Token), ct);

        return result.IsSuccess
            ? Results.Ok(result.Value)
            : Results.Problem(result.Error, statusCode: 401);
    }
}

public sealed record RefreshTokenRequest(string Token);
