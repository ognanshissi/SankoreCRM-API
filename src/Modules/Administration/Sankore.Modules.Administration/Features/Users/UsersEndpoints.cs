using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Sankore.Modules.Administration.Features.Users.CreateUser;
using Sankore.Modules.Administration.Features.Users.DeactivateUser;
using Sankore.Modules.Administration.Features.Users.Register;
using Sankore.Modules.Administration.Features.Users.ResetPassword;

namespace Sankore.Modules.Administration.Features.Users;

public static class UsersEndpoints
{
    public static IEndpointRouteBuilder MapUsersEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("users").WithTags("Users");

        return group.MapResetPassword()
            .MapCreateUser()
            .MapRegister()
            .MapDeactivateUser();
    }
}