using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Sankore.Modules.Administration.Features.PermissionsCatalog.ListPermissions;

namespace Sankore.Modules.Administration.Features.PermissionsCatalog;

internal static class PermissionsEndpoints
{
    internal static IEndpointRouteBuilder MapPermissionsEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("permissions").WithTags("Permissions");
        group.MapListPermissions();
        return app;
    }
}
