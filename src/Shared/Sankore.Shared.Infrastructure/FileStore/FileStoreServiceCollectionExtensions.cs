namespace Sankore.Shared.Infrastructure.FileStore;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Sankore.Shared.Kernel;

public static class FileStoreServiceCollectionExtensions
{
    /// <summary>
    /// Registers the local <see cref="IFileStore"/>. Uses TryAdd so every module
    /// needing a file store can call it without fighting over the registration,
    /// and so the host can substitute a different implementation beforehand.
    /// </summary>
    public static IServiceCollection AddLocalFileStore(this IServiceCollection services)
    {
        services.TryAddSingleton<IFileStore, LocalFileStore>();
        return services;
    }
}
