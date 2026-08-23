using MediatR;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Sankore.Shared.Kernel;

namespace Sankore.Modules.Administration.Features.Roles.ListRoles;

internal static class ListRolesEndpoint
{
    internal static IEndpointRouteBuilder MapListRoles(this IEndpointRouteBuilder app)
    {
        app.MapGet("/", async (ISender sender, CancellationToken ct) =>
        {
            var result = await sender.Send(new ListRolesQuery(), ct);
            return result.IsSuccess ? Results.Ok(result.Value) : Results.Problem(result.Error);
        })
        .WithName("ListRoles")
        .WithSummary("List all assignable roles")
        .WithDescription("Returns roles that can be assigned to users. Requires permission: user:read.")
        .RequireAuthorization(Permissions.CanReadUser.Code)
        .Produces<List<RoleDto>>(StatusCodes.Status200OK)
        .Produces(StatusCodes.Status401Unauthorized)
        .Produces(StatusCodes.Status403Forbidden)
        .WithOpenApi();

        return app;
    }
}
