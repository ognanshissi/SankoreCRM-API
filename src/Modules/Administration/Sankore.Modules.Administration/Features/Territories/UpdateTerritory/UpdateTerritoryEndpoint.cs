using MediatR;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Sankore.Shared.Infrastructure.Extensions;
using Sankore.Shared.Kernel;

namespace Sankore.Modules.Administration.Features.Territories.UpdateTerritory;

public static class UpdateTerritoryEndpoint
{
    public static IEndpointRouteBuilder MapUpdateTerritory(this IEndpointRouteBuilder app)
    {
        app.MapPut("territories/{id:guid}", Handle)
            .WithTags("Territories")
            .WithName("UpdateTerritory")
            .WithSummary("Update a territory's details")
            .WithDescription(
                "Updates the name, description, geographic center, radius, and product specialities. " +
                "Deactivated territories cannot be updated. " +
                "Requires permission: territory:update.")
            .RequireAuthorization(Permissions.CanUpdateTerritory.Code)
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
        UpdateTerritoryRequest req,
        ISender sender,
        CancellationToken ct)
    {
        var result = await sender.Send(new UpdateTerritoryCommand(
            TerritoryId: id,
            Name: req.Name,
            Description: req.Description ?? string.Empty,
            Latitude: req.Latitude,
            Longitude: req.Longitude,
            RayonKm: req.RayonKm,
            ProductSpecialities: req.ProductSpecialities), ct);

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

public sealed record UpdateTerritoryRequest(
    string Name,
    string? Description,
    double? Latitude,
    double? Longitude,
    double RayonKm,
    List<string> ProductSpecialities);
