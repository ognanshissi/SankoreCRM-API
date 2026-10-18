using MediatR;
using Sankore.Admin.Infrastructure.Auth;

namespace Sankore.Admin.Features.Auth.Login;

public static class LoginEndpoint
{
    public static void Map(RouteGroupBuilder group)
    {
        group.MapPost("/login", async (LoginRequest req, ISender sender, CancellationToken ct) =>
            {
                var result = await sender.Send(new LoginCommand(req.Email ?? string.Empty, req.Password ?? string.Empty), ct);
                return result.Tokens is { } tokens
                    ? Results.Ok(tokens)
                    : Results.Problem(result.Error, statusCode: StatusCodes.Status401Unauthorized);
            })
            .WithName("Login")
            .WithSummary("Sign a platform administrator in")
            .Produces<AdminAuthTokens>()
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status429TooManyRequests)
            .AllowAnonymous()
            .RequireRateLimiting(AdminAuthSetup.LoginRateLimit)
            .WithOpenApi();
    }

    public sealed record LoginRequest(string? Email, string? Password);
}
