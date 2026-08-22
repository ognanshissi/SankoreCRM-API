using MediatR;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Sankore.Shared.Infrastructure.Extensions;
using Sankore.Shared.Kernel;

namespace Sankore.Modules.Administration.Features.Territories.DeleteTerritory;

public static class DeleteTerritoryEndpoint
{
    public static IEndpointRouteBuilder MapDeleteTerritory(this IEndpointRouteBuilder app)
    {
        app.MapDelete("{id:guid}", Handle)
            .WithName("DeleteTerritory")
            .WithSummary("Deactivate (soft-delete) a territory")
            .WithDescription(
                "Marks the territory as inactive. Deactivated territories are excluded " +
                "from the list endpoint by default. This operation is idempotent-safe: " +
                "deleting an already-inactive territory returns 400. " +
                "Requires permission: territory:delete.")
            .RequireAuthorization(Permissions.CanDeleteTerritory.Code)
            .Produces(StatusCodes.Status204NoContent)
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden)
            .WithOpenApi()
            .WithTenantHeader();

        return app;
    }

    private static async Task<IResult> Handle(
        Guid id,
        ISender sender,
        CancellationToken ct)
    {
        var result = await sender.Send(new DeleteTerritoryCommand(id), ct);

        if (!result.IsSuccess)
        {
            var isNotFound = result.Error!.Contains("not found", StringComparison.OrdinalIgnoreCase);
            return isNotFound
                ? Results.NotFound(new { error = result.Error })
                : Results.Problem(result.Error, statusCode: StatusCodes.Status400BadRequest);
        }

        return Results.NoContent();
    }
}
