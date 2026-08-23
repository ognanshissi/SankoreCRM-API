using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Sankore.Modules.Administration.Features.Agencies.CreateAgency;
using Sankore.Modules.Administration.Features.Agencies.DeleteAgency;
using Sankore.Modules.Administration.Features.Agencies.GetAgency;
using Sankore.Modules.Administration.Features.Agencies.GetAgencyTree;
using Sankore.Modules.Administration.Features.Agencies.ListAgencies;
using Sankore.Modules.Administration.Features.Agencies.UpdateAgency;

namespace Sankore.Modules.Administration.Features.Agencies;

public static class AgenciesEndpoints
{
    public static IEndpointRouteBuilder MapAgenciesEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("agencies").WithTags("Agencies");

        return group
            .MapCreateAgency()
            .MapListAgencies()
            .MapGetAgencyTree()
            .MapGetAgency()
            .MapUpdateAgency()
            .MapDeleteAgency();
    }
}
