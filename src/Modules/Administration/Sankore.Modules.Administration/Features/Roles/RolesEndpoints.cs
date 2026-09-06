using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Sankore.Modules.Administration.Features.Roles.AssignPermissionToRole;
using Sankore.Modules.Administration.Features.Roles.CreateRole;
using Sankore.Modules.Administration.Features.Roles.DeleteRole;
using Sankore.Modules.Administration.Features.Roles.GetRole;
using Sankore.Modules.Administration.Features.Roles.ListRoles;
using Sankore.Modules.Administration.Features.Roles.RevokePermissionFromRole;
using Sankore.Modules.Administration.Features.Roles.UpdateRole;

namespace Sankore.Modules.Administration.Features.Roles;

internal static class RolesEndpoints
{
    internal static IEndpointRouteBuilder MapRolesEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("roles").WithTags("Roles");
        group.MapListRoles();
        group.MapGetRole();
        group.MapCreateRole();
        group.MapUpdateRole();
        group.MapDeleteRole();
        group.MapAssignPermissionToRole();
        group.MapRevokePermissionFromRole();
        return app;
    }
}
