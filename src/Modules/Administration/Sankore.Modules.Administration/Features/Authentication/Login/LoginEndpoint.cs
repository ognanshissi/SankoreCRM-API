using MediatR;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Sankore.Shared.Infrastructure.Extensions;

namespace Sankore.Modules.Administration.Features.Authentication.Login;

public static class LoginEndpoint
{
    public static IEndpointRouteBuilder MapLogin(this IEndpointRouteBuilder app)
    {
        app.MapPost("/auth/login", Handle)
            .WithTags("Auth")
            .WithName("Login")
            .AllowAnonymous()
            .WithTenantHeader();
        return app;
    }

    public static async Task<IResult> Handle(LoginRequest req, ISender sender, CancellationToken ct)
    {
        var result = await sender.Send(new LoginCommand(req.Email, req.Password), ct);

        return result.IsSuccess
            ? Results.Ok(result.Value)
            : Results.Problem(result.Error, statusCode: 401);
    }
}

public sealed record LoginRequest(Guid TenantId, string Email, string Password);
