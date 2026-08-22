using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Sankore.Modules.Administration.Features.Territories.CreateTerritory;
using Sankore.Modules.Administration.Features.Territories.DeleteTerritory;
using Sankore.Modules.Administration.Features.Territories.GetTerritory;
using Sankore.Modules.Administration.Features.Territories.ListTerritories;
using Sankore.Modules.Administration.Features.Territories.UpdateTerritory;

namespace Sankore.Modules.Administration.Features.Territories;

public static class TerritoriesEndpoints
{
    public static IEndpointRouteBuilder MapTerritoriesEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group =  endpoints.MapGroup("territories").WithTags("Territories");
        return group.MapCreateTerritory()
            .MapGetTerritory()
            .MapListTerritories()
            .MapUpdateTerritory()
            .MapDeleteTerritory();
    }
}