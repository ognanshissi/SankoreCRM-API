using MediatR;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Sankore.Shared.Kernel;

namespace Sankore.Modules.Administration.Features.Roles.AssignPermissionToRole;

internal static class AssignPermissionToRoleEndpoint
{
    internal static IEndpointRouteBuilder MapAssignPermissionToRole(this IEndpointRouteBuilder app)
    {
        app.MapPost("/{roleId:guid}/permissions", async (
                Guid roleId,
                AssignPermissionToRoleRequest body,
                ISender sender,
                CancellationToken ct) =>
            {
                var result = await sender.Send(new AssignPermissionToRoleCommand(roleId, body.PermissionCode), ct);
                return result.IsSuccess ? Results.NoContent() : Results.Problem(result.Error);
            })
            .WithName("AssignPermissionToRole")
            .WithSummary("Grant a permission to a custom role")
            .RequireAuthorization(Permissions.CanManageRolePermissions.Code)
            .Produces(StatusCodes.Status204NoContent)
            .Produces(StatusCodes.Status400BadRequest)
            .WithOpenApi();

        return app;
    }
}

internal sealed record AssignPermissionToRoleRequest(string PermissionCode);
