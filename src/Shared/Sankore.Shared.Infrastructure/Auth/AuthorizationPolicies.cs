using System.Reflection.Metadata.Ecma335;
using Sankore.Shared.Kernel;

namespace Sankore.Shared.Infrastructure.Auth;

using Microsoft.Extensions.DependencyInjection;

/// <summary>
/// Central registry of authorization policies. Each module contributes its
/// own policies via a Configure* method called from here, so the full list
/// of "who can do what" stays discoverable in one place while ownership of
/// each policy's definition still lives inside the owning module.
/// </summary>
public static class AuthorizationPolicies
{
    public static IServiceCollection AddSankoreAuthorization(this IServiceCollection services)
    {
        services.AddAuthorization(options =>
        {
            foreach (var permission in Permissions.All)
            {
                options.AddPolicy(permission.Code, p => p
                    .RequireAuthenticatedUser()
                    .RequireClaim("permission", permission.Code));
            }
        });

        return services;
    }
}
