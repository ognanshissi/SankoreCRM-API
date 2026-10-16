namespace Sankore.Modules.Integration.Adapters.Ringover;

using Sankore.Modules.Integration.Infrastructure;
using Sankore.Modules.Integration.PublicApi;
using Sankore.Shared.Kernel;

/// <summary>Whether this connection has its two secrets in the vault.</summary>
public enum RingoverCredentialState
{
    Present,

    /// <summary>No API key. The adapter cannot call the provider at all.</summary>
    ApiKeyMissing,

    /// <summary>
    /// An API key but no webhook token. Told apart from the first because they break different
    /// halves: without the key nothing can be asked of the provider, while without the token the
    /// provider's events are refused at the door — so an agent could place a call and never see it
    /// journalled, which looks like a different fault entirely.
    /// </summary>
    WebhookSecretMissing,
}

/// <summary>
/// The half of TEL-B-04 that needs nothing from the provider: WHERE the two secrets come from.
///
/// <para>
/// <b>Both are per-CONNECTION secrets in the M12 vault</b>, under
/// <c>IntegrationSecrets.CredentialKey(tenantId, connectionId)</c> for the API key and
/// <c>IntegrationSecrets.WebhookSecretKey(tenantId, connectionId)</c> for the webhook token —
/// names that already existed before this adapter. Keyed per connection and not per tenant
/// because an IMF has a core banking system, two insurers and a telephony provider at once, and a
/// tenant-wide key would make saving the second credential destroy the first.
/// <c>RingoverSettings</c> carries coordinates only: the API host and the default caller number.
/// No value reaches a column, an endpoint, an event or an audit row.
/// </para>
///
/// <para>
/// <b>It asks the vault for a HINT and never for the value.</b> Nothing can be done with the key
/// until the status vocabulary and the error bodies are known, so a value read here would be a
/// secret decrypted into the memory of a process that has no use for it — gratuitous exposure, on
/// a path a logger or an exception filter could later widen. The hint answers exactly the question
/// this adapter is entitled to ask: is one stored.
/// </para>
///
/// <para>
/// <b>Why probe at all when every call refuses anyway.</b> Because "no API key is configured" is a
/// mistake the tenant's own administrator fixes in a minute, while "no test account" is a
/// procurement conversation, and collapsing the two sends the wrong person to the wrong screen.
/// Probing also exercises the key shapes, the scope and the connection-keying today, so the day
/// the transport is written the credential is not where the mistake is.
/// </para>
///
/// <para>
/// <c>internal</c>, unlike the matrix and the specification: it reaches
/// <see cref="IntegrationSecrets"/>, which the module exposes to adapter assemblies only. Keeping
/// it internal says the vault keys are the adapter's own plumbing and not something a host or a
/// screen should be computing.
/// </para>
/// </summary>
internal static class RingoverCredential
{
    /// <summary>
    /// Which secrets are stored for this connection. Never returns, logs or otherwise
    /// materialises a value.
    /// </summary>
    /// <remarks>
    /// An expired entry counts as present. The hint carries <c>ExpiresAt</c> and it is
    /// deliberately not consulted: <c>ISecretsModule.GetValueAsync</c> hands out a stored value
    /// regardless of its expiry, so a probe that called an expired key missing would report a
    /// state the transport will not see — sending an administrator to re-enter a key that still
    /// authenticates. Expiry is the vault's business to enforce, in one place, for every module.
    /// </remarks>
    public static async Task<RingoverCredentialState> ProbeAsync(
        ISecretsModule secrets, Guid tenantId, Guid connectionId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(secrets);

        var apiKey = await secrets.GetHintAsync(
            IntegrationSecrets.CredentialKey(tenantId, connectionId), ct);

        if (apiKey is null) return RingoverCredentialState.ApiKeyMissing;

        var webhook = await secrets.GetHintAsync(
            IntegrationSecrets.WebhookSecretKey(tenantId, connectionId), ct);

        return webhook is null
            ? RingoverCredentialState.WebhookSecretMissing
            : RingoverCredentialState.Present;
    }

    /// <summary>
    /// The <c>Detail</c> a connection with a missing secret is refused with, reported as
    /// <see cref="IntegrationErrors.CredentialMissing"/>.
    ///
    /// <para>
    /// It says what is missing, who fixes it, and — explicitly — that this is not the test
    /// account. Without that last clause the message is read as one more variation on "waiting for
    /// Ringover", and a connection stays broken for a reason nobody owns.
    /// </para>
    /// </summary>
    public static string MissingDetail(RingoverCredentialState state)
        => state switch
        {
            RingoverCredentialState.WebhookSecretMissing =>
                "No webhook token is stored in the vault for this Ringover connection. Save it "
                + "through the connection's secret endpoint (INT-03); it is held per connection, "
                + "so a token saved for another connection of the same tenant does not serve this "
                + "one. Without it the provider's call events are refused at the door — an agent "
                + "would place calls and see none of them journalled. This is not the missing test "
                + "account; nothing is being waited on from Ringover for it.",

            _ =>
                "No Ringover API key is stored in the vault for this connection. Save it through "
                + "the connection's secret endpoint (INT-03); it is held per connection, so a key "
                + "saved for another connection of the same tenant does not serve this one. This "
                + "is not the missing test account; nothing is being waited on from Ringover for "
                + "it.",
        };
}
