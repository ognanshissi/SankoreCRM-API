using MediatR;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Sankore.Shared.Kernel;

namespace Sankore.Modules.Administration.Features.Users.RevokeRole;

internal static class RevokeRoleEndpoint
{
    internal static IEndpointRouteBuilder MapRevokeRole(this IEndpointRouteBuilder app)
    {
        app.MapPost("{userId:guid}/revoke-role", async (
            Guid userId,
            RevokeRoleRequest req,
            ISender sender,
            CancellationToken ct) =>
        {
            var result = await sender.Send(new RevokeRoleCommand(userId, req.RoleId), ct);
            return result.IsSuccess
                ? Results.NoContent()
                : Results.Problem(result.Error, statusCode: StatusCodes.Status400BadRequest);
        })
        .WithName("RevokeRoleFromUser")
        .WithSummary("Revoke a role from a user")
        .WithDescription("Removes a role from a user. The System role cannot be revoked. Requires permission: user:revoke-role.")
        .RequireAuthorization(Permissions.CanRevokeRole.Code)
        .Produces(StatusCodes.Status204NoContent)
        .Produces(StatusCodes.Status400BadRequest)
        .Produces(StatusCodes.Status401Unauthorized)
        .Produces(StatusCodes.Status403Forbidden)
        .WithOpenApi();

        return app;
    }
}

public sealed record RevokeRoleRequest(Guid RoleId);
