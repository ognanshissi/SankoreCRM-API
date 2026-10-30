using Sankore.Shared.Infrastructure.Auth;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Sankore.Modules.Administration.Features.Users.AdminResetPassword;
using Sankore.Modules.Administration.Features.Users.AgentSettings;
using Sankore.Modules.Administration.Features.Users.AssignManager;
using Sankore.Modules.Administration.Features.Users.AssignRole;
using Sankore.Modules.Administration.Features.Users.BulkAssign;
using Sankore.Modules.Administration.Features.Users.AssignScopedPermission;
using Sankore.Modules.Administration.Features.Users.ChangePassword;
using Sankore.Modules.Administration.Features.Users.CreateUser;
using Sankore.Modules.Administration.Features.Users.DeactivateUser;
using Sankore.Modules.Administration.Features.Users.GetUser;
using Sankore.Modules.Administration.Features.Users.Impersonation;
using Sankore.Modules.Administration.Features.Users.Invitation;
using Sankore.Modules.Administration.Features.Users.GetUserPermissions;
using Sankore.Modules.Administration.Features.Users.GetUserRoles;
using Sankore.Modules.Administration.Features.Users.GetUserStatusStats;
using Sankore.Modules.Administration.Features.Users.ListUsers;
using Sankore.Modules.Administration.Features.Users.MyProfile;
using Sankore.Modules.Administration.Features.Users.ReactivateUser;
using Sankore.Modules.Administration.Features.Users.Register;
using Sankore.Modules.Administration.Features.Users.RevokeRole;
using Sankore.Modules.Administration.Features.Users.RevokeScopedPermission;
using Sankore.Modules.Administration.Features.Users.Security;
using Sankore.Modules.Administration.Features.Users.UpdateUser;

namespace Sankore.Modules.Administration.Features.Users;

public static class UsersEndpoints
{
    public static IEndpointRouteBuilder MapUsersEndpoints(this IEndpointRouteBuilder app)
    {
        // The caller's own profile, avatar, password and agent settings stay editable in test mode:
        // they write the caller's own account, whatever the mode.
        var group = app.MapGroup("users").WithTags("Users")
            .LiveModeWritesOnly(http => http.Request.Path.Value?.Contains("/users/me", StringComparison.OrdinalIgnoreCase) == true);

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
            .MapRevokeScopedPermission()
            .MapChangePassword()
            .MapAdminResetPassword()
            .MapBulkAssign()
            .MapAssignManager()
            .MapGetMyAgentSettings()
            .MapUpdateMyAgentSettings()
            .MapSetAgentCapacityCeiling()
            .MapGetMyProfile()
            .MapUpdateMyProfile()
            .MapMyAvatar()
            .MapMySecurity()
            .MapResendInvitation()
            .MapImpersonation();
    }
}
