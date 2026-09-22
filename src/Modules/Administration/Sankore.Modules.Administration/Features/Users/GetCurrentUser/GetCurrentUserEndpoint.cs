namespace Sankore.Modules.Administration.Features.Users.GetCurrentUser;

using MediatR;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

public static class GetCurrentUserEndpoint
{
    public static IEndpointRouteBuilder MapGetCurrentUser(this IEndpointRouteBuilder app)
    {
        app.MapGet("users/auth-context", Handle)
            .WithName("GetCurrentUser")
            .WithTags("Users")
            .WithSummary("Returns the connected user's profile, roles and permissions")
            .RequireAuthorization()
            .Produces<CurrentUserDto>()
            .Produces(StatusCodes.Status401Unauthorized)
            .WithOpenApi();

        return app;
    }

    private static async Task<IResult> Handle(ISender sender, CancellationToken ct)
    {
        var result = await sender.Send(new GetCurrentUserQuery(), ct);

        return result.IsSuccess
            ? Results.Ok(result.Value)
            : Results.Unauthorized();
    }
}
