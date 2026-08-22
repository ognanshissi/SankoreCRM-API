using MediatR;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Sankore.Shared.Infrastructure.Extensions;
using Sankore.Shared.Kernel;

namespace Sankore.Modules.Administration.Features.Territories.CreateTerritory;

public static class CreateTerritoryEndpoint
{
    public static IEndpointRouteBuilder MapCreateTerritory(this IEndpointRouteBuilder app)
    {
        app.MapPost("/", Handle)
            .WithName("CreateTerritory")
            .WithSummary("Create a new territory")
            .WithDescription(
                "Creates a territory with a geographic center point and coverage radius. " +
                "Code must be unique within the tenant. " +
                "Requires permission: territory:create.")
            // .RequireAuthorization(Permissions.CanCreateTerritory.Code)
            .Produces<CreateTerritoryResult>(StatusCodes.Status201Created)
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden)
            .WithOpenApi()
            .WithTenantHeader();

        return app;
    }

    private static async Task<IResult> Handle(
        CreateTerritoryRequest req,
        ISender sender,
        CancellationToken ct)
    {
        var result = await sender.Send(new CreateTerritoryCommand(
            Name: req.Name,
            Code: req.Code,
            Description: req.Description ?? string.Empty,
            Latitude: req.Latitude,
            Longitude: req.Longitude,
            RayonKm: req.RayonKm,
            ProductSpecialities: req.ProductSpecialities), ct);

        return result.IsSuccess
            ? Results.Created($"/api/administration/territories/{result.Value.TerritoryId}", result.Value)
            : Results.Problem(result.Error, statusCode: StatusCodes.Status400BadRequest);
    }
}

public sealed record CreateTerritoryRequest(
    string Name,
    string Code,
    string? Description,
    double? Latitude,
    double? Longitude,
    double RayonKm,
    List<string> ProductSpecialities);
