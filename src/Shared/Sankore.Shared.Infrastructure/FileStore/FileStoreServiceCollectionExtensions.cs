namespace Sankore.Shared.Infrastructure.FileStore;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Sankore.Shared.Kernel;
using Sankore.Shared.ObjectStorage;

/// <summary>
/// Names this store's slot in the object store. A constant rather than a literal at each call
/// site, for the same reason <c>KycObjectStorage.Concern</c> is one: it is both the DI key and,
/// for a bucket deployment, the bucket — "one bucket per concern" stops being true the moment two
/// spellings of the same word exist.
/// </summary>
public static class FileStoreObjectStorage
{
    public const string Concern = "imports";
}

public static class FileStoreServiceCollectionExtensions
{
    /// <summary>
    /// Registers the <see cref="IFileStore"/> used for import uploads and generated exports, on
    /// a filesystem <see cref="IObjectBackend"/> rooted at <c>FileStore:BasePath</c>.
    ///
    /// <para>
    /// Everything is TryAdd: four modules call this (Leads, Customers, Administration) without
    /// fighting over the registration, and a host can substitute either half beforehand — a
    /// different <see cref="IFileStore"/>, or just a different backend under the same key, which
    /// is the whole point of the split and how this store moves to R2 without being rewritten.
    /// </para>
    /// </summary>
    public static IServiceCollection AddLocalFileStore(this IServiceCollection services)
    {
        services.AddOptions<FileStoreOptions>().BindConfiguration(FileStoreOptions.SectionName);

        services.TryAddKeyedSingleton<IObjectBackend>(FileStoreObjectStorage.Concern, (sp, _) =>
        {
            var options = sp.GetRequiredService<IOptions<FileStoreOptions>>().Value;
            var logger = sp.GetRequiredService<ILogger<LocalObjectBackend>>();

            return new LocalObjectBackend(options.ResolveBasePath(), logger);
        });

        services.TryAddSingleton<IFileStore>(sp => new ObjectBackedFileStore(
            sp.GetRequiredKeyedService<IObjectBackend>(FileStoreObjectStorage.Concern),
            sp.GetRequiredService<IOptions<FileStoreOptions>>(),
            sp.GetRequiredService<ILogger<ObjectBackedFileStore>>()));

        return services;
    }
}
