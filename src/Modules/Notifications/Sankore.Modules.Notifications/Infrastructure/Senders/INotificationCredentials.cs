namespace Sankore.Modules.Notifications.Infrastructure.Senders;

using Sankore.Modules.Administration.PublicApi;
using Sankore.Modules.Notifications.Infrastructure.Providers;
using Sankore.Shared.Kernel;

/// <summary>
/// Fetches a tenant's email credential from the vault at send time.
///
/// Its own type rather than an inline call so the senders share one answer to "which key?" and
/// so a test can hand them a credential without standing up an encryption vault.
/// </summary>
internal interface INotificationCredentials
{
    Task<string?> GetAsync(Guid tenantId, ResolvedEmailProvider provider, CancellationToken ct);
}

internal sealed class VaultNotificationCredentials(ISecretsModule secrets) : INotificationCredentials
{
    public Task<string?> GetAsync(Guid tenantId, ResolvedEmailProvider provider, CancellationToken ct)
        => provider.HasCredential
            ? secrets.GetValueAsync(NotificationSecrets.CredentialKey(tenantId, provider.ProviderType), ct)
            // Nothing was ever stored: skip the vault round-trip entirely rather than ask it for
            // a key we know is absent.
            : Task.FromResult<string?>(null);
}
