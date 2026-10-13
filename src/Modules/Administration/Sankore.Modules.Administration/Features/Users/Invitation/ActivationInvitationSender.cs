using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;
using Sankore.Modules.Administration.Domain;
using Sankore.Modules.Administration.Infrastructure.Identity;
using Sankore.Modules.Notifications.PublicApi;
using Sankore.Shared.Kernel;

namespace Sankore.Modules.Administration.Features.Users.Invitation;

/// <summary>
/// Envoie le lien d'activation d'un compte. Une seule écriture du lien et du courriel, pour la
/// création, le renvoi et le changement d'adresse : trois copies auraient fini par différer sur
/// l'URL, et c'est l'URL que l'écran d'activation doit savoir lire.
/// </summary>
internal sealed class ActivationInvitationSender(
    UserManager<AppUser> userManager,
    ITenantStore tenantStore,
    INotificationsModule notifications,
    IOptions<IdentityTokenOptions> tokenOptions,
    TimeProvider clock)
{
    public TimeSpan Lifespan => tokenOptions.Value.ActivationTokenLifespan;

    /// <param name="invalidatePrevious">
    /// Vrai pour un renvoi : la rotation du tampon de sécurité rend inutilisables les liens déjà
    /// envoyés, y compris celui parti à une ancienne adresse.
    /// </param>
    /// <returns>L'instant d'envoi ; le lien expire à cet instant plus <see cref="Lifespan"/>.</returns>
    public async Task<DateTimeOffset> SendAsync(AppUser user, string locale, bool invalidatePrevious, CancellationToken ct)
    {
        if (invalidatePrevious)
            await userManager.UpdateSecurityStampAsync(user);

        var token = await userManager.GenerateUserTokenAsync(user, ActivationTokens.ProviderName, ActivationTokens.Purpose);
        var tenantInfo = await tenantStore.GetAsync(user.TenantId, ct);
        var sentAt = clock.GetUtcNow();

        await notifications.QueueEmailAsync(new QueueEmailRequest(
            TemplateKey:    "user.activation",
            RecipientEmail: user.Email!,
            RecipientName:  user.FullName,
            Module:         "Administration",
            Locale:         locale,
            TemplateData: new Dictionary<string, object>
            {
                ["full_name"]      = user.FullName,
                ["activation_url"] = $"{tenantInfo?.Fqdn}/auth/account-activation?token={Uri.EscapeDataString(token)}&userId={user.Id}&RequestType=AccountActivation",
                ["company_name"]   = tenantInfo?.Name ?? "",
                ["tenant_id"]      = user.TenantId.ToString(),
                ["user_id"]        = user.Id.ToString(),
            },
            // Le premier envoi garde sa clé d'origine ; chaque renvoi est un courriel distinct.
            IdempotencyKey: invalidatePrevious
                ? $"user-activation-{user.Id}-{sentAt.ToUnixTimeMilliseconds()}"
                : $"user-activation-{user.Id}",
            TenantId:       user.TenantId), ct);

        user.MarkInvitationSent(sentAt);
        return sentAt;
    }
}
