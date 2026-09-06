using MediatR;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Sankore.Shared.Infrastructure.Extensions;
using Sankore.Shared.Kernel;

namespace Sankore.Modules.Administration.Features.Users.GetUser;

public static class GetUserEndpoint
{
    public static IEndpointRouteBuilder MapGetUser(this IEndpointRouteBuilder app)
    {
        app.MapGet("{userId:guid}", Handle)
            .WithTags("Users")
            .WithName("GetUser")
            .WithSummary("Get a user by ID")
            .RequireAuthorization(Permissions.CanReadUser.Code)
            .Produces<UserDto>()
            .Produces(StatusCodes.Status404NotFound)
            .WithOpenApi()
            .WithTenantHeader();

        return app;
    }

    private static async Task<IResult> Handle(Guid userId, ISender sender, CancellationToken ct)
    {
        var result = await sender.Send(new GetUserQuery(userId), ct);

        return result.IsSuccess
            ? Results.Ok(result.Value)
            : Results.NotFound(result.Error);
    }
}
