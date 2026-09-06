using MediatR;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Sankore.Shared.Infrastructure.Extensions;
using Sankore.Shared.Kernel;

namespace Sankore.Modules.Administration.Features.Users.ReactivateUser;

public static class ReactivateUserEndpoint
{
    public static IEndpointRouteBuilder MapReactivateUser(this IEndpointRouteBuilder app)
    {
        app.MapPost("{userId:guid}/reactivate", Handle)
            .WithTags("Users")
            .WithName("ReactivateUser")
            .WithSummary("Reactivate a disabled user")
            .RequireAuthorization(Permissions.CanReactivateUser.Code)
            .Produces(StatusCodes.Status204NoContent)
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status404NotFound)
            .WithOpenApi()
            .WithTenantHeader();

        return app;
    }

    private static async Task<IResult> Handle(
        Guid userId,
        ISender sender,
        CancellationToken ct)
    {
        var result = await sender.Send(new ReactivateUserCommand(userId), ct);

        return result.IsSuccess
            ? Results.NoContent()
            : Results.Problem(result.Error, statusCode: StatusCodes.Status400BadRequest);
    }
}
