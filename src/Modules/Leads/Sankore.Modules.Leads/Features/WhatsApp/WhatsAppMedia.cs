namespace Sankore.Modules.Leads.Features.WhatsApp;

using MassTransit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Sankore.Modules.Integration.PublicApi;
using Sankore.Modules.Leads.Infrastructure;
using Sankore.Modules.Leads.Infrastructure.WhatsAppMedia;
using Sankore.Shared.Infrastructure.BackgroundJobs;
using Sankore.Shared.Kernel;

/// <summary>
/// A customer's media arrived (WA-BE-20): its consumer keeps an envelope-encrypted copy, so the
/// thread still opens it after Meta stops serving it (thirty days) and the retention can delete it.
/// </summary>
public sealed record WhatsAppMediaReceivedEvent(Guid TenantId, Guid MessageId) : IntegrationEventBase;

/// <summary>
/// Opens a message's media: our encrypted copy first, the provider otherwise — the one read behind
/// the lead's thread and the conversation detail, so both serve the same bytes under the same type
/// rules.
/// </summary>
internal sealed class WhatsAppMediaReader(IServiceProvider services, IIntegrationModule integration, ILogger<WhatsAppMediaReader> logger)
{
    /// <summary>What is shown or offered for download. Anything else is refused, not served.</summary>
    internal static readonly string[] AllowedPrefixes =
        ["image/jpeg", "image/png", "image/webp", "application/pdf", "audio/", "video/mp4", "video/3gpp"];

    public async Task<Result<MessagingMediaContent>> ReadAsync(
        Guid tenantId, Guid connectionId, string? storageRef, string? mediaId, string? mimeType, string? fileName,
        bool purged, CancellationToken ct)
    {
        if (purged) return Result.Fail<MessagingMediaContent>(WhatsAppErrors.MediaPurged);

        if (storageRef is not null && services.GetService<IWhatsAppMediaStore>() is { } store)
        {
            var bytes = await store.OpenAsync(tenantId, storageRef, ct);

            if (bytes is not null && mimeType is not null)
                return Checked(new MessagingMediaContent(bytes, mimeType, fileName));

            logger.LogWarning("WhatsApp media {StorageRef} is missing from the store; falling back to the provider.", storageRef);
        }

        if (mediaId is null) return Result.Fail<MessagingMediaContent>(WhatsAppErrors.NoMedia);

        var media = await integration.Messaging.DownloadMediaAsync(tenantId, connectionId, mediaId, ct);
        if (media.IsFailure) return Result.Fail<MessagingMediaContent>(media.Code ?? WhatsAppErrors.NoMedia);

        return Checked(new MessagingMediaContent(media.Value.Content, media.Value.ContentType, fileName ?? media.Value.FileName));
    }

    private static Result<MessagingMediaContent> Checked(MessagingMediaContent media)
    {
        var type = media.ContentType.Split(';')[0].Trim().ToLowerInvariant();

        return AllowedPrefixes.Any(p => type.StartsWith(p, StringComparison.Ordinal))
            ? Result.Ok(media with { ContentType = type })
            : Result.Fail<MessagingMediaContent>(IntegrationErrors.MessagingMediaRefused);
    }
}

/// <summary>
/// Fetches an inbound media from the provider and stores it encrypted (WA-BE-20). Without a store
/// configured on this deployment it does nothing, and the thread keeps fetching from the provider.
/// A failure is retried by the broker; a media already stored, or already purged, is left alone.
/// </summary>
public sealed class StoreWhatsAppMediaConsumer(IServiceScopeFactory scopeFactory, ILogger<StoreWhatsAppMediaConsumer> logger)
    : IConsumer<WhatsAppMediaReceivedEvent>
{
    public async Task Consume(ConsumeContext<WhatsAppMediaReceivedEvent> context)
    {
        var (tenantId, messageId) = (context.Message.TenantId, context.Message.MessageId);
        if (tenantId == Guid.Empty || messageId == Guid.Empty) return;

        using var bgCtx = BackgroundJobContext.SetScope(tenantId, Guid.Empty, "SYSTEM");
        using var scope = scopeFactory.CreateScope();
        var sp = scope.ServiceProvider;

        if (sp.GetService<IWhatsAppMediaStore>() is not { } store) return;

        var db = sp.GetRequiredService<LeadsDbContext>();
        var message = await db.WhatsAppMessages.AsTracking().FirstOrDefaultAsync(m => m.Id == messageId, context.CancellationToken);

        if (message is null || message.MediaStorageRef is not null || message.MediaPurgedAt is not null || message.MediaId is null)
            return;

        var connectionId = await db.WhatsAppConversations
            .Where(c => c.Id == message.ConversationId)
            .Select(c => (Guid?)c.ConnectionId)
            .FirstOrDefaultAsync(context.CancellationToken);

        if (connectionId is null) return;

        var media = await sp.GetRequiredService<IIntegrationModule>().Messaging
            .DownloadMediaAsync(tenantId, connectionId.Value, message.MediaId, context.CancellationToken);

        if (media.IsFailure)
        {
            // Refused for good (too big, unknown id): nothing to keep, the thread will say so when
            // opened. Transient: thrown so the broker retries.
            if (media.Family == ErrorFamily.Transient)
                throw new InvalidOperationException($"WhatsApp media {messageId} could not be fetched ({media.Code}).");

            logger.LogWarning("WhatsApp media {MessageId} was not stored: {Code}.", messageId, media.Code);
            return;
        }

        var stored = await store.StoreAsync(tenantId, media.Value.Content, context.CancellationToken);
        message.AttachStoredMedia(stored.StorageRef, stored.SizeBytes, stored.Sha256, sp.GetRequiredService<TimeProvider>().GetUtcNow());

        try
        {
            await db.SaveChangesAsync(context.CancellationToken);
        }
        catch (DbUpdateException)
        {
            // Lost the reference: the object would be an orphan nobody purges.
            await store.DeleteAsync(tenantId, stored.StorageRef, CancellationToken.None);
            throw;
        }
    }
}
