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
            options.AddPolicy("Leads.Capture", p => p
                .RequireAuthenticatedUser()
                .RequireClaim("permission", "leads:capture"));
            options.AddPolicy("Leads.Dispatch", p => p
                .RequireAuthenticatedUser()
                .RequireClaim("permission", "leads:dispatch")
                .RequireClaim("mfa_verified", "true"));
            options.AddPolicy("Leads.Reassign", p => p
                .RequireAuthenticatedUser()
                .RequireRole("BranchManager", "SalesManager"));
            options.AddPolicy("Leads.ViewPipeline", p => p
                .RequireAuthenticatedUser()
                .RequireClaim("permission", "leads:read"));
        });

        return services;
    }
}
