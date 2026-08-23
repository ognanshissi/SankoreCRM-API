using MediatR;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Sankore.Modules.Administration.Features.Territories;
using Sankore.Shared.Infrastructure.Extensions;
using Sankore.Shared.Kernel;

namespace Sankore.Modules.Administration.Features.Territories.ListTerritories;

public static class ListTerritoriesEndpoint
{
    public static IEndpointRouteBuilder MapListTerritories(this IEndpointRouteBuilder app)
    {
        app.MapGet(string.Empty, Handle)
            .WithName("ListTerritories")
            .WithSummary("List all territories for the current tenant")
            .WithDescription("Returns active territories by default. Pass includeInactive=true to include deactivated ones.")
            .RequireAuthorization(Permissions.CanReadTerritory.Code)
            .Produces<List<TerritoryDto>>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden)
            .WithOpenApi()
            .WithTenantHeader();

        return app;
    }

    private static async Task<IResult> Handle(
        ISender sender,
        bool includeInactive,
        CancellationToken ct)
    {
        var result = await sender.Send(new ListTerritoriesQuery(includeInactive), ct);
        return Results.Ok(result.Value);
    }
}
