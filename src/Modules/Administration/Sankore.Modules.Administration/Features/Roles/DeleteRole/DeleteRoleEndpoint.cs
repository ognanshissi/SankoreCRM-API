using MediatR;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Sankore.Shared.Kernel;

namespace Sankore.Modules.Administration.Features.Roles.DeleteRole;

internal static class DeleteRoleEndpoint
{
    internal static IEndpointRouteBuilder MapDeleteRole(this IEndpointRouteBuilder app)
    {
        app.MapDelete("/{roleId:guid}", async (Guid roleId, ISender sender, CancellationToken ct) =>
        {
            var result = await sender.Send(new DeleteRoleCommand(roleId), ct);
            return result.IsSuccess ? Results.NoContent() : Results.Problem(result.Error);
        })
        .WithName("DeleteRole")
        .WithSummary("Delete a custom role (must have no assigned users)")
        .RequireAuthorization(Permissions.CanDeleteRole.Code)
        .Produces(StatusCodes.Status204NoContent)
        .Produces(StatusCodes.Status400BadRequest)
        .WithOpenApi();

        return app;
    }
}
