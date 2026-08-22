using MediatR;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Sankore.Modules.Administration.Features.Territories;
using Sankore.Shared.Infrastructure.Extensions;
using Sankore.Shared.Kernel;

namespace Sankore.Modules.Administration.Features.Territories.GetTerritory;

public static class GetTerritoryEndpoint
{
    public static IEndpointRouteBuilder MapGetTerritory(this IEndpointRouteBuilder app)
    {
        app.MapGet("{id:guid}", Handle)
            .WithName("GetTerritory")
            .WithSummary("Get a territory by ID")
            .RequireAuthorization(Permissions.CanReadTerritory.Code)
            .Produces<TerritoryDto>(StatusCodes.Status200OK)
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
        var result = await sender.Send(new GetTerritoryQuery(id), ct);

        return result.IsSuccess
            ? Results.Ok(result.Value)
            : Results.NotFound(new { error = result.Error });
    }
}
