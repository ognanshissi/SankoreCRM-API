namespace Sankore.Modules.Notifications.Infrastructure.Senders;

using Microsoft.Extensions.Options;
using Sankore.Modules.Administration.PublicApi;
using Sankore.Modules.Notifications.Infrastructure.Providers;
using Sankore.Shared.Kernel;

/// <summary>
/// Fetches the email credential at send time: the tenant's, from the vault — or, when the tenant
/// sends through the platform account, the platform's, from <see cref="PlatformEmailOptions"/>.
///
/// Its own type rather than an inline call so the senders share one answer to "which key?" and
/// so a test can hand them a credential without standing up an encryption vault.
/// </summary>
internal interface INotificationCredentials
{
    Task<string?> GetAsync(Guid tenantId, ResolvedEmailProvider provider, CancellationToken ct);
}

internal sealed class VaultNotificationCredentials(
    ISecretsModule secrets, IOptions<PlatformEmailOptions> platform) : INotificationCredentials
{
    public Task<string?> GetAsync(Guid tenantId, ResolvedEmailProvider provider, CancellationToken ct)
        => provider.IsDefault
            // Never the tenant's vault here, even when it holds a key: a tenant back on the
            // platform default may still have a Brevo key stored from before, and sending through
            // the platform route with it would bill the tenant's account for the platform's mail.
            ? Task.FromResult(platform.Value.BrevoApiKey)
            : provider.HasCredential
            ? secrets.GetValueAsync(NotificationSecrets.CredentialKey(tenantId, provider.ProviderType), ct)
            // Nothing was ever stored: skip the vault round-trip entirely rather than ask it for
            // a key we know is absent.
            : Task.FromResult<string?>(null);
}
