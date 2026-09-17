using MediatR;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Sankore.Shared.Kernel;

namespace Sankore.Modules.Administration.Features.Users.GetUserRoles;

internal static class GetUserRolesEndpoint
{
    internal static IEndpointRouteBuilder MapGetUserRoles(this IEndpointRouteBuilder app)
    {
        app.MapGet("{userId:guid}/roles", async (
            Guid userId, ISender sender, CancellationToken ct) =>
        {
            var result = await sender.Send(new GetUserRolesQuery(userId), ct);
            return result.IsSuccess
                ? Results.Ok(result.Value)
                : Results.NotFound(new { error = result.Error });
        })
        .WithName("GetUserRoles")
        .WithSummary("Get all active roles assigned to a user")
        .RequireAuthorization(Shared.Kernel.Permissions.CanReadUser.Code)
        .Produces<List<UserRoleDto>>(StatusCodes.Status200OK)
        .Produces(StatusCodes.Status404NotFound)
        .Produces(StatusCodes.Status401Unauthorized)
        .Produces(StatusCodes.Status403Forbidden)
        .WithOpenApi();

        return app;
    }
}
