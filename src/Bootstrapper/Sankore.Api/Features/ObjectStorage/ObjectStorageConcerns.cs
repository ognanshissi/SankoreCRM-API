namespace Sankore.Api.Features.ObjectStorage;

using Sankore.Modules.Integration.Infrastructure.BatchStorage;
using Sankore.Modules.Integration.Infrastructure.ClaimStorage;
using Sankore.Modules.Integration.Infrastructure.RecordingStorage;

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
        // Call recordings (TEL-B-16). A FIFTH concern, and the one whose contents are a person's
        // own voice: its own key, its own retention (frozen per file at retrieval rather than per
        // dossier) and its own audience. Encrypted above the backend by EncryptedRecordingStore,
        // so what lands here is ciphertext whichever medium backs it.
        new(RecordingStorage.Concern, RecordingStorage.ResolveBasePath),
        // WhatsApp media (WA lot 4). Envelope-encrypted above the backend by
        // EncryptedWhatsAppMediaStore: a customer's photographs, documents and voice notes.
        new(Sankore.Modules.Leads.Infrastructure.WhatsAppMedia.WhatsAppMediaStorage.Concern,
            Sankore.Modules.Leads.Infrastructure.WhatsAppMedia.WhatsAppMediaStorage.ResolveBasePath),

        // EncryptedTaskAttachmentStore: the files agents attach to a task.
        new(Sankore.Modules.Leads.Infrastructure.TaskAttachments.TaskAttachmentStorage.Concern,
            Sankore.Modules.Leads.Infrastructure.TaskAttachments.TaskAttachmentStorage.ResolveBasePath),
    ];

    internal static Concern? Find(string? name) =>
        All.FirstOrDefault(c => string.Equals(c.Name, name, StringComparison.Ordinal));

    internal static string Names => string.Join(", ", All.Select(c => c.Name));
}
