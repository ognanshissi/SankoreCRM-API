namespace Sankore.Api.Features.ObjectStorage;

using Sankore.Modules.Integration.Infrastructure.BatchStorage;
using Sankore.Modules.Integration.Infrastructure.ClaimStorage;

using Sankore.Modules.Kyc;
using Sankore.Shared.Infrastructure.FileStore;

internal static class ObjectStorageConcerns
{
    internal sealed record Concern(string Name, Func<IConfiguration, IHostEnvironment, string> ResolveLocalRoot);

    internal static readonly Concern[] All =
    [
        new(KycModule.ObjectStorageConcern, KycModule.ResolveObjectStorageBasePath),
        new(FileStoreObjectStorage.Concern, (config, _) => FileStoreOptions.ResolveBasePath(config)),
        // Outbound and inbound batch files (INT-24). Encrypted above the backend by
        // EncryptedBatchFileStore, so what lands here is ciphertext whichever medium backs it.
        new(IntegrationBatchStorage.Concern, IntegrationBatchStorage.ResolveBasePath),
        // Pièces de sinistre (ASS-09). Its own concern and not a folder inside the batch one:
        // a claim piece has a different retention, a different key and a different audience, and
        // one bucket holding both would make a single leaked credential a leak of both.
        // Encrypted above the backend by EncryptedClaimDocumentStore, so what lands here is
        // ciphertext whichever medium backs it.
        new(ClaimDocumentStorage.Concern, ClaimDocumentStorage.ResolveBasePath),
    ];

    internal static Concern? Find(string? name) =>
        All.FirstOrDefault(c => string.Equals(c.Name, name, StringComparison.Ordinal));

    internal static string Names => string.Join(", ", All.Select(c => c.Name));
}
