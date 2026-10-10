namespace Sankore.Modules.Integration.Adapters.Sab;

using Sankore.Modules.Integration.Infrastructure;
using Sankore.Modules.Integration.PublicApi;
using Sankore.Shared.Kernel;

/// <summary>Whether this connection has an Open SAB API key in the vault.</summary>
public enum SabCredentialState
{
    Present,

    /// <summary>
    /// Nothing is stored. A configuration fault the tenant's own administrator clears by saving
    /// the key through INT-03's secret endpoint — never the pending catalogue.
    /// </summary>
    Missing
}

/// <summary>
/// The half of criterion 1 that does not need the catalogue: WHERE the API key comes from
/// (« l'adaptateur implémente les ports sur l'API Open SAB, authentifiée par clé API »).
///
/// <para>
/// <b>The key is a per-tenant secret and lives in the M12 vault</b>, under
/// <c>IntegrationSecrets.CredentialKey(tenantId, connectionId)</c> — the same name the Temenos
/// authenticator reads, keyed per CONNECTION and not per tenant because an IMF has a core banking
/// system and two insurers at once, and a tenant-wide key would make saving the second credential
/// destroy the first. <c>SabSettings</c> carries coordinates only: the base URL, the entity, and a
/// vault reference. The value reaches no column, no endpoint, no event and no audit row.
/// </para>
///
/// <para>
/// <b>It asks the vault for a HINT and never for the value</b>, and that is a decision rather than
/// a shortcut. Nothing can be done with an Open SAB API key until the catalogue says how it
/// travels on the wire, so a value read here would be a secret decrypted into the memory of a
/// process that has no use for it — gratuitous exposure, on a path a logger or an exception
/// filter could later widen. The hint answers exactly the question this adapter is entitled to
/// ask: is one stored. The day the catalogue arrives, the value read is one line
/// (<c>GetValueAsync</c> on this very key) and it belongs in the transport, next to the header it
/// fills — which is where <c>TemenosAuthenticator</c> puts it, and the reason that adapter sets
/// the credential per request instead of on <c>DefaultRequestHeaders</c> of a pooled client.
/// </para>
///
/// <para>
/// <b>Why probe at all when every call refuses anyway.</b> Because "no API key is configured for
/// this connection" is a mistake the tenant's administrator can fix in a minute, while "no Open
/// SAB catalogue" is a procurement conversation, and collapsing the two sends the wrong person to
/// the wrong screen. Probing also exercises the key shape, the scope and the connection-keying
/// today, so the day the transport is written the credential is not where the mistake is.
/// </para>
///
/// <para>
/// <c>internal</c>, unlike the matrix and the entity rule: it reaches
/// <see cref="IntegrationSecrets"/>, which the module exposes to adapter assemblies only. Keeping
/// the type internal says the vault key is the adapter's own plumbing and not something a host or
/// a screen should be computing.
/// </para>
/// </summary>
internal static class SabCredential
{
    /// <summary>
    /// Whether a credential is stored for this connection. Never returns, logs or otherwise
    /// materialises the value.
    /// </summary>
    /// <remarks>
    /// An expired entry counts as <see cref="SabCredentialState.Present"/>. The hint carries
    /// <c>ExpiresAt</c>, and it is deliberately not consulted: <c>ISecretsModule.GetValueAsync</c>
    /// hands out a stored value regardless of its expiry, so a probe that called an expired key
    /// missing would report a state the transport will not see — sending an administrator to
    /// re-enter a key that still authenticates. Expiry is the vault's business to enforce, in one
    /// place, for every module.
    /// </remarks>
    public static async Task<SabCredentialState> ProbeAsync(
        ISecretsModule secrets, Guid tenantId, Guid connectionId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(secrets);

        var hint = await secrets.GetHintAsync(
            IntegrationSecrets.CredentialKey(tenantId, connectionId), ct);

        return hint is null ? SabCredentialState.Missing : SabCredentialState.Present;
    }

    /// <summary>
    /// The <c>Detail</c> a connection with no stored key is refused with, reported as
    /// <see cref="IntegrationErrors.CredentialMissing"/>.
    ///
    /// <para>
    /// It says what is missing, who fixes it, and — explicitly — that this is not the catalogue.
    /// Without that last clause the message is read as one more variation on "waiting for SBS",
    /// and a connection stays broken for a reason nobody owns.
    /// </para>
    /// </summary>
    public static string MissingDetail()
        => "No Open SAB API key is stored in the vault for this SAB connection. Save it through "
           + "the connection's secret endpoint (INT-03); it is held per connection, so a key saved "
           + "for another connection of the same tenant does not serve this one. This is not the "
           + "missing Open SAB catalogue — nothing is being waited on from SBS for it.";
}
