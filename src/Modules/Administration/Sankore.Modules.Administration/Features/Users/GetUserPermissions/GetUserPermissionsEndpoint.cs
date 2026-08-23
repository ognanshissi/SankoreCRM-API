using MediatR;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Sankore.Shared.Kernel;

namespace Sankore.Modules.Administration.Features.Users.GetUserPermissions;

internal static class GetUserPermissionsEndpoint
{
    internal static IEndpointRouteBuilder MapGetUserPermissions(this IEndpointRouteBuilder app)
    {
        app.MapGet("{userId:guid}/permissions", async (
            Guid userId, ISender sender, CancellationToken ct) =>
        {
            var result = await sender.Send(new GetUserPermissionsQuery(userId), ct);
            return result.IsSuccess
                ? Results.Ok(result.Value)
                : Results.NotFound(new { error = result.Error });
        })
        .WithName("GetUserPermissions")
        .WithSummary("Get a user's effective permissions")
        .WithDescription(
            "Returns the union of role-based permissions and active scoped attributions for a user. " +
            "Requires permission: user:read.")
        .RequireAuthorization(Permissions.CanReadUser.Code)
        .Produces<UserPermissionsDto>(StatusCodes.Status200OK)
        .Produces(StatusCodes.Status401Unauthorized)
        .Produces(StatusCodes.Status403Forbidden)
        .Produces(StatusCodes.Status404NotFound)
        .WithOpenApi();

        return app;
    }
}
