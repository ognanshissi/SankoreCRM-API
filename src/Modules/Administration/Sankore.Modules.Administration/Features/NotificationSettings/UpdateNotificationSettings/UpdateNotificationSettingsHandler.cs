using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.DependencyInjection;
using Sankore.Modules.Administration.Domain;
using Sankore.Modules.Administration.Infrastructure;
using Sankore.Modules.Administration.PublicApi;
using Sankore.Shared.Infrastructure.Auth;
using Sankore.Shared.Infrastructure.Messaging;
using Sankore.Shared.Kernel;

namespace Sankore.Modules.Administration.Features.NotificationSettings.UpdateNotificationSettings;

internal sealed class UpdateNotificationSettingsHandler(
    AdministrationDbContext db,
    ICurrentUser currentUser,
    ISecretsModule secrets,
    [FromKeyedServices(nameof(AdministrationDbContext))] IEventPublisher publisher,
    IDistributedCache cache)
    : IRequestHandler<UpdateNotificationSettingsCommand, Result>
{
    public async Task<Result> Handle(
        UpdateNotificationSettingsCommand request, CancellationToken ct)
    {
        var tenantId = currentUser.TenantId;

        // AsTracking is not optional here: the DbContext is NoTracking by default, so without it
        // an UPDATE of an existing row mutates a detached entity and SaveChangesAsync writes
        // nothing — the settings appear to save and silently do not.
        var settings = await db.TenantNotificationSettings.AsTracking().FirstOrDefaultAsync(ct);

        if (settings is null)
        {
            settings = TenantNotificationSettings.CreateDefault(tenantId, currentUser.Id);
            db.TenantNotificationSettings.Add(settings);
        }

        // Captured BEFORE UpdateProvider overwrites it — the credential decision below depends
        // on whether the provider actually changed.
        var previousProvider = settings.ProviderType;

        var smtp = request.ProviderType == "Smtp" && !string.IsNullOrWhiteSpace(request.SmtpHost)
            ? new SmtpRelaySettings(
                request.SmtpHost.Trim(),
                request.SmtpPort ?? DefaultPortFor(request.SmtpUseSsl),
                string.IsNullOrWhiteSpace(request.SmtpUsername) ? null : request.SmtpUsername.Trim(),
                request.SmtpUseSsl,
                request.SmtpUseStartTls)
            : null;

        settings.UpdateProvider(
            request.ProviderType,
            request.FromEmail,
            request.FromName,
            request.ReplyToEmail,
            request.SendingDomain,
            smtp,
            currentUser.Id);

        // ── The credential ──────────────────────────────────────────────────
        // Written to the vault BEFORE the row is saved. If the vault write throws, the
        // TransactionScope opened by TransactionBehavior rolls the settings back, so the tenant
        // is never left claiming a provider whose credential was never stored.
        if (!string.IsNullOrWhiteSpace(request.Credential))
        {
            await secrets.SetAsync(
                NotificationSecrets.CredentialKey(tenantId, request.ProviderType),
                request.Credential,
                expiresAt: null,
                ct);

            settings.SetCredentialPresence(true, currentUser.Id);
        }
        else if (previousProvider != request.ProviderType)
        {
            // Switching provider without supplying a credential: whatever was stored belongs to
            // the OLD provider, so this one has none until an administrator provides it.
            var existing = await secrets.GetHintAsync(
                NotificationSecrets.CredentialKey(tenantId, request.ProviderType), ct);

            settings.SetCredentialPresence(existing is not null, currentUser.Id);
        }

        await publisher.PublishAsync(new TenantNotificationSettingsChangedEvent(tenantId), ct);
        await db.SaveChangesAsync(ct);

        // Best-effort: the consumer invalidates it too, and the entry expires on its own.
        await cache.RemoveAsync($"notifications:provider:{tenantId}", ct);

        return Result.Ok();
    }

    /// <summary>465 is implicit TLS, 587 is STARTTLS — the two ports nobody should have to recall.</summary>
    private static int DefaultPortFor(bool useSsl) => useSsl ? 465 : 587;
}
