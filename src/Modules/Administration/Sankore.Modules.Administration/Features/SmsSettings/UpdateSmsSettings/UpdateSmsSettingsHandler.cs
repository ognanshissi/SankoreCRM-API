namespace Sankore.Modules.Administration.Features.SmsSettings.UpdateSmsSettings;

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

/// <summary>
/// The SMS twin of <c>UpdateNotificationSettingsHandler</c>, and deliberately the same shape: the
/// credential goes to the vault BEFORE the row is saved, so a vault failure rolls the settings
/// back through <c>TransactionBehavior</c>'s scope and a tenant is never left claiming a provider
/// whose secret was never stored.
/// </summary>
internal sealed class UpdateSmsSettingsHandler(
    AdministrationDbContext db,
    ICurrentUser currentUser,
    ISecretsModule secrets,
    [FromKeyedServices(nameof(AdministrationDbContext))] IEventPublisher publisher,
    IDistributedCache cache)
    : IRequestHandler<UpdateSmsSettingsCommand, Result>
{
    /// <summary>
    /// The cache entry M08's resolver reads. Spelled here and in
    /// <c>CachedSmsProviderResolver</c>; a reader and a writer that disagree about a key never
    /// invalidate, and the only symptom is a fifteen-minute delay that looks like flakiness.
    /// </summary>
    internal static string CacheKey(Guid tenantId) => $"notifications:sms-provider:{tenantId}";

    public async Task<Result> Handle(UpdateSmsSettingsCommand request, CancellationToken ct)
    {
        var tenantId = currentUser.TenantId;

        // AsTracking is not optional: the DbContext is NoTracking by default, so without it an
        // UPDATE mutates a detached entity and SaveChangesAsync writes nothing — the settings
        // appear to save and silently do not. The email handler carries the same note.
        var settings = await db.TenantSmsSettings.AsTracking().FirstOrDefaultAsync(ct);

        if (settings is null)
        {
            settings = TenantSmsSettings.CreateDefault(tenantId, currentUser.Id);
            db.TenantSmsSettings.Add(settings);
        }

        // Captured BEFORE the update: the credential decision below depends on whether the
        // provider actually changed.
        var previousProvider = settings.ProviderType;

        if (request.ProviderType == TenantSmsSettings.DisabledProvider)
        {
            // Non-destructive: the sender id, the SID and the vault entry are left alone so the
            // channel can be switched back on without re-entering a key. See the entity.
            settings.Disable(currentUser.Id);
        }
        else
        {
            settings.UpdateProvider(
                request.ProviderType,
                request.SenderId,
                // Only Twilio has one. Passing Brevo's null here is what clears a SID left behind
                // by a previous Twilio configuration.
                TenantSmsSettings.UsesAccountSid(request.ProviderType) ? request.AccountSid : null,
                request.DefaultCallingCode,
                currentUser.Id);
        }

        // ── The credential ──────────────────────────────────────────────────
        if (!string.IsNullOrWhiteSpace(request.Credential))
        {
            await secrets.SetAsync(
                // SmsCredentialKey and NOT CredentialKey: Brevo sells both channels, and a shared
                // name would make storing an SMS key overwrite the tenant's email key. See the
                // helper's own remarks.
                NotificationSecrets.SmsCredentialKey(tenantId, request.ProviderType),
                request.Credential,
                expiresAt: null,
                ct);

            settings.SetCredentialPresence(true, currentUser.Id);
        }
        else if (previousProvider != request.ProviderType
                 && request.ProviderType != TenantSmsSettings.DisabledProvider)
        {
            // Switching provider without supplying a credential: whatever is stored belongs to the
            // OLD provider, so this one has none until an administrator provides it. Asked of the
            // vault rather than assumed, because a tenant switching BACK to a provider they used
            // before still has its key.
            var existing = await secrets.GetHintAsync(
                NotificationSecrets.SmsCredentialKey(tenantId, request.ProviderType), ct);

            settings.SetCredentialPresence(existing is not null, currentUser.Id);
        }

        await publisher.PublishAsync(new TenantSmsSettingsChangedEvent(tenantId), ct);
        await db.SaveChangesAsync(ct);

        // Best-effort: the consumer drops it too, and the entry expires on its own.
        await cache.RemoveAsync(CacheKey(tenantId), ct);

        return Result.Ok();
    }
}
