using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Sankore.Modules.Administration.Features.Users.AssignRole;
using Sankore.Modules.Administration.Features.Users.AssignScopedPermission;
using Sankore.Modules.Administration.Features.Users.CreateUser;
using Sankore.Modules.Administration.Features.Users.DeactivateUser;
using Sankore.Modules.Administration.Features.Users.GetUser;
using Sankore.Modules.Administration.Features.Users.GetUserPermissions;
using Sankore.Modules.Administration.Features.Users.GetUserRoles;
using Sankore.Modules.Administration.Features.Users.GetUserStatusStats;
using Sankore.Modules.Administration.Features.Users.ListUsers;
using Sankore.Modules.Administration.Features.Users.ReactivateUser;
using Sankore.Modules.Administration.Features.Users.Register;
using Sankore.Modules.Administration.Features.Users.RevokeRole;
using Sankore.Modules.Administration.Features.Users.RevokeScopedPermission;
using Sankore.Modules.Administration.Features.Users.UpdateUser;

namespace Sankore.Modules.Administration.Features.Users;

public static class UsersEndpoints
{
    public static IEndpointRouteBuilder MapUsersEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("users").WithTags("Users");

        return group
            .MapGetUserStatusStats()
            .MapListUsers()
            .MapCreateUser()
            .MapGetUser()
            .MapUpdateUser()
            .MapRegister()
            .MapDeactivateUser()
            .MapReactivateUser()
            .MapAssignRole()
            .MapRevokeRole()
            .MapGetUserRoles()
            .MapGetUserPermissions()
            .MapAssignScopedPermission()
            .MapRevokeScopedPermission();
    }
}
