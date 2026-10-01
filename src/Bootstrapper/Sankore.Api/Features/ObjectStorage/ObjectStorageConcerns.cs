namespace Sankore.Api.Features.ObjectStorage;

using Sankore.Modules.Kyc;
using Sankore.Shared.Infrastructure.FileStore;

/// <summary>
/// The concerns this host stores objects under, and where each one's files live when no bucket is
/// configured.
///
/// <para>
/// One list, read in two places: <c>Program.cs</c> declares a backend per entry at start-up, and
/// the migration endpoint resolves the source root from the same entry. Two lists would be two
/// chances for the migration to read a different folder from the one the application writes —
/// which is the single way this feature can silently move nothing and report success.
/// </para>
///
/// <para>
/// The SDK files are deliberately absent. They are public-read JavaScript served straight off the
/// filesystem, with no tenant, no encryption and no audit; putting them in a private bucket would
/// mean proxying every &lt;script&gt; tag an embedded form loads.
/// </para>
/// </summary>
internal static class ObjectStorageConcerns
{
    internal sealed record Concern(string Name, Func<IConfiguration, IHostEnvironment, string> ResolveLocalRoot);

    internal static readonly Concern[] All =
    [
        new(KycModule.ObjectStorageConcern, KycModule.ResolveObjectStorageBasePath),

        // FileStore resolves from configuration alone — its default is a temp folder, not the
        // content root — so the environment argument is discarded rather than faked.
        new(FileStoreObjectStorage.Concern, (config, _) => FileStoreOptions.ResolveBasePath(config)),
    ];

    internal static Concern? Find(string? name) =>
        All.FirstOrDefault(c => string.Equals(c.Name, name, StringComparison.Ordinal));

    internal static string Names => string.Join(", ", All.Select(c => c.Name));
}
