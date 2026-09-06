using MediatR;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Sankore.Shared.Kernel;

namespace Sankore.Modules.Administration.Features.Roles.RevokePermissionFromRole;

internal static class RevokePermissionFromRoleEndpoint
{
    internal static IEndpointRouteBuilder MapRevokePermissionFromRole(this IEndpointRouteBuilder app)
    {
        app.MapDelete("/{roleId:guid}/permissions/{permissionCode}", async (
                Guid roleId,
                string permissionCode,
                ISender sender,
                CancellationToken ct) =>
            {
                var result = await sender.Send(new RevokePermissionFromRoleCommand(roleId, permissionCode), ct);
                return result.IsSuccess ? Results.NoContent() : Results.Problem(result.Error);
            })
            .WithName("RevokePermissionFromRole")
            .WithSummary("Revoke a permission from a custom role")
            .RequireAuthorization(Permissions.CanManageRolePermissions.Code)
            .Produces(StatusCodes.Status204NoContent)
            .Produces(StatusCodes.Status400BadRequest)
            .WithOpenApi();

        return app;
    }
}
