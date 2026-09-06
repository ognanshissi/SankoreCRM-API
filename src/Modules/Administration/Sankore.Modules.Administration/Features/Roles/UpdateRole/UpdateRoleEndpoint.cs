using MediatR;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Sankore.Shared.Kernel;

namespace Sankore.Modules.Administration.Features.Roles.UpdateRole;

internal static class UpdateRoleEndpoint
{
    internal static IEndpointRouteBuilder MapUpdateRole(this IEndpointRouteBuilder app)
    {
        app.MapPut("/{roleId:guid}", async (Guid roleId, UpdateRoleRequest body, ISender sender, CancellationToken ct) =>
        {
            var result = await sender.Send(new UpdateRoleCommand(roleId, body.Label), ct);
            return result.IsSuccess ? Results.NoContent() : Results.Problem(result.Error);
        })
        .WithName("UpdateRole")
        .WithSummary("Update a custom role's label")
        .RequireAuthorization(Permissions.CanUpdateRole.Code)
        .Produces(StatusCodes.Status204NoContent)
        .Produces(StatusCodes.Status404NotFound)
        .WithOpenApi();

        return app;
    }
}

internal sealed record UpdateRoleRequest(string Label);
