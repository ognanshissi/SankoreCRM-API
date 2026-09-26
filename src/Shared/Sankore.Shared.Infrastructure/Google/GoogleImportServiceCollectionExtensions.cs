namespace Sankore.Shared.Infrastructure.Google;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

public static class GoogleImportServiceCollectionExtensions
{
    /// <summary>
    /// Binds <see cref="GoogleImportSettings"/> to the "GoogleImport" section.
    /// Safe to call from every module that needs it — binding the same section
    /// twice is a no-op.
    /// </summary>
    public static IServiceCollection AddGoogleImportSettings(
        this IServiceCollection services, IConfiguration config)
    {
        services.Configure<GoogleImportSettings>(config.GetSection("GoogleImport"));
        return services;
    }
}
