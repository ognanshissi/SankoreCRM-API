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

        // EncryptedClientDocumentStore: the files filed on a client record. Missing from this list
        // until 2026-11-13, so CustomersModule's local fallback always won and these documents
        // stayed on the container's disk even with a bucket configured.
        new(Sankore.Modules.Customers.Infrastructure.Documents.ClientDocumentStorage.Concern,
            Sankore.Modules.Customers.Infrastructure.Documents.ClientDocumentStorage.ResolveBasePath),

        // The institution's logo, IN CLEAR: it is public (served to anyone by public/branding and
        // shown in e-mails). Was a bytea column until 2026-11-13.
        new(Sankore.Modules.Administration.Infrastructure.Branding.CompanyLogoStorage.Concern,
            Sankore.Modules.Administration.Infrastructure.Branding.CompanyLogoStorage.ResolveBasePath),

        // EncryptedUserAvatarStore: users' profile photos, a person's face — their own key.
        // Was a bytea column until 2026-11-13.
        new(Sankore.Modules.Administration.Infrastructure.Branding.UserAvatarStorage.Concern,
            Sankore.Modules.Administration.Infrastructure.Branding.UserAvatarStorage.ResolveBasePath),

        // The web-form SDK scripts (public, served by the API with their SRI hash). On the disk until
        // 2026-11-13, which required a /data/sdk volume; the builds shipped in the image are copied
        // into the bucket at start-up (ShippedSdkFiles).
        new(Sankore.Modules.Leads.Features.LeadSources.Sdk.SdkFileStorage.Concern,
            Sankore.Modules.Leads.Features.LeadSources.Sdk.SdkFileStorage.ResolveBasePath),
    ];

    internal static Concern? Find(string? name) =>
        All.FirstOrDefault(c => string.Equals(c.Name, name, StringComparison.Ordinal));

    internal static string Names => string.Join(", ", All.Select(c => c.Name));
}
