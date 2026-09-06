using MediatR;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Sankore.Shared.Kernel;

namespace Sankore.Modules.Administration.Features.Roles.GetRole;

internal static class GetRoleEndpoint
{
    internal static IEndpointRouteBuilder MapGetRole(this IEndpointRouteBuilder app)
    {
        app.MapGet("/{roleId:guid}", async (Guid roleId, ISender sender, CancellationToken ct) =>
        {
            var result = await sender.Send(new GetRoleQuery(roleId), ct);
            return result.IsSuccess ? Results.Ok(result.Value) : Results.NotFound(result.Error);
        })
        .WithName("GetRole")
        .WithSummary("Get role with its permissions")
        .RequireAuthorization(Permissions.CanReadRole.Code)
        .Produces<RoleDetailDto>(StatusCodes.Status200OK)
        .Produces(StatusCodes.Status404NotFound)
        .WithOpenApi();

        return app;
    }
}
