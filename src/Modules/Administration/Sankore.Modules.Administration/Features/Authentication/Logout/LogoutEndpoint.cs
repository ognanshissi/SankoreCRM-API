using MediatR;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using System.Security.Claims;

namespace Sankore.Modules.Administration.Features.Authentication.Logout;

public static class LogoutEndpoint
{
    public static IEndpointRouteBuilder MapLogout(this IEndpointRouteBuilder app)
    {
        app.MapPost("/auth/logout", Handle)
            .WithTags("Auth")
            .WithName("Logout")
            .Produces(StatusCodes.Status204NoContent)
            .RequireAuthorization();
        return app;
    }

    public static async Task<IResult> Handle(ClaimsPrincipal user, ISender sender, CancellationToken ct)
    {
        var userId = Guid.Parse(user.FindFirstValue(ClaimTypes.NameIdentifier)!);
        var result = await sender.Send(new LogoutCommand(userId), ct);

        return result.IsSuccess
            ? Results.NoContent()
            : Results.Problem(result.Error, statusCode: 400);
    }
}