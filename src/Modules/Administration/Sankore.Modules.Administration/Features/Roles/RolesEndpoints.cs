using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Sankore.Modules.Administration.Features.Roles.ListRoles;

namespace Sankore.Modules.Administration.Features.Roles;

internal static class RolesEndpoints
{
    internal static IEndpointRouteBuilder MapRolesEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("roles").WithTags("Roles");
        group.MapListRoles();
        return app;
    }
}
