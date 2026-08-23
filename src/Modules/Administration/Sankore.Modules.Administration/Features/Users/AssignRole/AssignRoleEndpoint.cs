using MediatR;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Sankore.Shared.Kernel;

namespace Sankore.Modules.Administration.Features.Users.AssignRole;

internal static class AssignRoleEndpoint
{
    internal static IEndpointRouteBuilder MapAssignRole(this IEndpointRouteBuilder app)
    {
        app.MapPost("{userId:guid}/assign-role", async (
            Guid userId,
            AssignRoleRequest req,
            ISender sender,
            CancellationToken ct) =>
        {
            var result = await sender.Send(new AssignRoleCommand(userId, req.RoleId), ct);
            return result.IsSuccess
                ? Results.NoContent()
                : Results.Problem(result.Error, statusCode: StatusCodes.Status400BadRequest);
        })
        .WithName("AssignRoleToUser")
        .WithSummary("Assign a role to a user")
        .WithDescription("Adds a role to an existing user. Requires permission: user:assign-role.")
        .RequireAuthorization(Permissions.CanAssignRole.Code)
        .Produces(StatusCodes.Status204NoContent)
        .Produces(StatusCodes.Status400BadRequest)
        .Produces(StatusCodes.Status401Unauthorized)
        .Produces(StatusCodes.Status403Forbidden)
        .WithOpenApi();

        return app;
    }
}

public sealed record AssignRoleRequest(Guid RoleId);
