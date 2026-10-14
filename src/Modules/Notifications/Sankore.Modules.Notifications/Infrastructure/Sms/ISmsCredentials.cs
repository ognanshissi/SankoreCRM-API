namespace Sankore.Modules.Notifications.Infrastructure.Sms;

using Sankore.Modules.Administration.PublicApi;
using Sankore.Shared.Kernel;

/// <summary>
/// Fetches a tenant's SMS secret from the vault at send time — Brevo's API key, Twilio's Auth
/// Token.
///
/// <para>
/// Its own type for the reason <c>INotificationCredentials</c> is one: the senders share a single
/// answer to "which key?", and a test can hand them a credential without standing up an
/// encryption vault.
/// </para>
/// </summary>
internal interface ISmsCredentials
{
    Task<string?> GetAsync(Guid tenantId, ResolvedSmsProvider provider, CancellationToken ct);
}

internal sealed class VaultSmsCredentials(ISecretsModule secrets) : ISmsCredentials
{
    public Task<string?> GetAsync(
        Guid tenantId, ResolvedSmsProvider provider, CancellationToken ct)
        => provider.HasCredential
            // SmsCredentialKey, not CredentialKey: Brevo sells both channels and a shared secret
            // name would have the two keys overwrite each other. See NotificationSecrets.
            ? secrets.GetValueAsync(
                NotificationSecrets.SmsCredentialKey(tenantId, provider.ProviderType), ct)
            // Nothing was ever stored: skip the vault round-trip rather than ask for a key we know
            // is absent.
            : Task.FromResult<string?>(null);
}
