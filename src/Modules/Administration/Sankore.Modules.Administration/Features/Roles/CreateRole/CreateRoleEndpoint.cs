using MediatR;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Sankore.Shared.Kernel;

namespace Sankore.Modules.Administration.Features.Roles.CreateRole;

internal static class CreateRoleEndpoint
{
    internal static IEndpointRouteBuilder MapCreateRole(this IEndpointRouteBuilder app)
    {
        app.MapPost("/", async (CreateRoleCommand command, ISender sender, CancellationToken ct) =>
        {
            var result = await sender.Send(command, ct);
            return result.IsSuccess
                ? Results.Created($"/api/v1/roles/{result.Value}", new { id = result.Value })
                : Results.Problem(result.Error);
        })
        .WithName("CreateRole")
        .WithSummary("Create a custom tenant role")
        .RequireAuthorization(Permissions.CanCreateRole.Code)
        .Produces(StatusCodes.Status201Created)
        .Produces(StatusCodes.Status400BadRequest)
        .WithOpenApi();

        return app;
    }
}
