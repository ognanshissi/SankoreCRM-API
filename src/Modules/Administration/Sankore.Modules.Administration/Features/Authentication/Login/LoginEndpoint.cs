using MediatR;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Sankore.Modules.Administration.Features.Authentication.Login;

public static class LoginEndpoint
{
    public static IEndpointRouteBuilder MapLogin(this IEndpointRouteBuilder app)
    {
        app.MapPost("/auth/login", Handle)
            .WithTags("Auth")
            .WithName("Login")
            .Produces<LoginResult>(StatusCodes.Status200OK)
            .AllowAnonymous()
            .RequireRateLimiting("auth");
        return app;
    }

    public static async Task<IResult> Handle(
        LoginRequest req, HttpContext http, ISender sender, CancellationToken ct)
    {
        // Taken from the connection and the headers, never from the body: the point of a login
        // history is to record where the request actually came from.
        var result = await sender.Send(
            new LoginCommand(
                req.Email,
                req.Password,
                IpAddress: http.Connection.RemoteIpAddress?.ToString(),
                UserAgent: http.Request.Headers.UserAgent.ToString()),
            ct);

        return result.IsSuccess
            ? Results.Ok(result.Value)
            : Results.Problem(result.Error, statusCode: 401);
    }
}

public sealed record LoginRequest(string Email, string Password);
