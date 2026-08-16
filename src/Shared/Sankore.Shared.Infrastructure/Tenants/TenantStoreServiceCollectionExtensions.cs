using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Sankore.Shared.Kernel;

namespace Sankore.Shared.Infrastructure.Tenants;

public static class TenantStoreServiceCollectionExtensions
{
    /// <summary>
    /// Registers the external tenant store, its in-memory cache decorator,
    /// and the named HttpClient. Call once from Program.cs.
    ///
    /// Expects configuration section "TenantStore:BaseUrl".
    /// </summary>
    public static IServiceCollection AddTenantStore(
        this IServiceCollection services,
        IConfiguration config)
    {
        services.Configure<TenantStoreOptions>(config.GetSection(TenantStoreOptions.Section));

        services.AddMemoryCache();

        services.AddHttpClient("TenantStore", (sp, client) =>
        {
            var opts = config.GetSection(TenantStoreOptions.Section).Get<TenantStoreOptions>()!;
            client.BaseAddress = new Uri(opts.BaseUrl.TrimEnd('/') + "/");
        });

        // Inner (HTTP) implementation, then wrapped by the caching decorator.
        services.AddSingleton<HttpTenantStore>();
        services.AddSingleton<ITenantStore, CachedTenantStore>(sp =>
            new CachedTenantStore(
                sp.GetRequiredService<HttpTenantStore>(),
                sp.GetRequiredService<Microsoft.Extensions.Caching.Memory.IMemoryCache>(),
                sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<TenantStoreOptions>>()));

        return services;
    }

    /// <summary>
    /// Adds <see cref="TenantResolutionMiddleware"/> to the pipeline.
    /// Must be called AFTER app.UseAuthentication().
    /// </summary>
    public static IApplicationBuilder UseTenantResolution(this IApplicationBuilder app)
        => app.UseMiddleware<TenantResolutionMiddleware>();
}
