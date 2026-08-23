using MediatR;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Sankore.Shared.Kernel;

namespace Sankore.Modules.Administration.Features.Users.RevokeScopedPermission;

internal static class RevokeScopedPermissionEndpoint
{
    internal static IEndpointRouteBuilder MapRevokeScopedPermission(this IEndpointRouteBuilder app)
    {
        app.MapDelete("{userId:guid}/permissions/{attributionId:guid}", async (
            Guid userId,
            Guid attributionId,
            ISender sender,
            CancellationToken ct) =>
        {
            var result = await sender.Send(new RevokeScopedPermissionCommand(userId, attributionId), ct);
            return result.IsSuccess
                ? Results.NoContent()
                : Results.Problem(result.Error, statusCode: StatusCodes.Status400BadRequest);
        })
        .WithName("RevokeScopedPermission")
        .WithSummary("Revoke a scoped permission attribution")
        .WithDescription(
            "Marks the attribution as inactive (soft revoke). " +
            "Requires permission: user:revoke-permission.")
        .RequireAuthorization(Permissions.CanRevokePermission.Code)
        .Produces(StatusCodes.Status204NoContent)
        .Produces(StatusCodes.Status400BadRequest)
        .Produces(StatusCodes.Status401Unauthorized)
        .Produces(StatusCodes.Status403Forbidden)
        .WithOpenApi();

        return app;
    }
}
