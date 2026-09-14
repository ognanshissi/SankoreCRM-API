using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Sankore.Shared.Kernel;

namespace Sankore.Shared.Infrastructure.Localization;

public static class LanguageServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="ILanguageContext"/> resolved from the current HTTP request.
    /// Call once from Program.cs alongside other cross-cutting infrastructure.
    /// </summary>
    public static IServiceCollection AddLanguageResolution(this IServiceCollection services)
    {
        services.AddScoped<ILanguageContext, HttpLanguageContext>();
        return services;
    }

    /// <summary>
    /// Adds <see cref="LanguageResolutionMiddleware"/> to the pipeline.
    /// Must be called AFTER app.UseAuthentication() and app.UseTenantResolution().
    /// </summary>
    public static IApplicationBuilder UseLanguageResolution(this IApplicationBuilder app)
        => app.UseMiddleware<LanguageResolutionMiddleware>();
}
