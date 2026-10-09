namespace Sankore.Modules.Integration.Adapters.Orass;

using Sankore.Modules.Integration.Infrastructure;
using Sankore.Modules.Integration.PublicApi;
using Sankore.Shared.Kernel;

/// <summary>Whether this connection has, in the vault, the credential its carrier needs.</summary>
public enum OrassCredentialState
{
    Present,

    /// <summary>
    /// Something the carrier needs is not stored. A configuration fault the tenant's own
    /// administrator clears by saving it through INT-03's secret endpoint — never the pending
    /// specification.
    /// </summary>
    Missing
}

/// <summary>A state and, when it is <see cref="OrassCredentialState.Missing"/>, what is missing.</summary>
internal readonly record struct OrassCredentialProbe(OrassCredentialState State, string? MissingDetail);

/// <summary>
/// The half of criterion 3 that does not need the specification: WHERE the credential comes from
/// (« référence de secret »).
///
/// <para>
/// <b>Credentials are per-tenant secrets and live in the M12 vault</b>, under
/// <c>IntegrationSecrets</c>'s per-CONNECTION keys — keyed per connection and not per tenant
/// because an IMF has a core banking system and two insurers at once, and a tenant-wide key would
/// make saving the second credential destroy the first. <c>OrassSettings</c> carries coordinates
/// only: the base URL, the branch, the intermediary code, the file coordinates, and a vault
/// reference. No value reaches a column, an endpoint, an event or an audit row.
/// </para>
///
/// ───────────────────────────────────────────────────────────────────────────────────────────────
/// <para>
/// <b>WHICH CREDENTIAL IS NEEDED DEPENDS ON THE CARRIER — and ORASS is the first adapter in this
/// module for which that is true.</b> SAB has one carrier and therefore one key. Criterion 2 gives
/// ORASS two, and they authenticate against different things:
/// </para>
///
/// <list type="bullet">
/// <item><see cref="OrassCarrier.ExternalApi"/> → the API credential,
///   <c>IntegrationSecrets.CredentialKey</c>. The same name the Temenos authenticator and the SAB
///   adapter read, which is the point of it being one name: an adapter's credential is "the
///   credential of this connection", not "the credential of this product".</item>
/// <item><see cref="OrassCarrier.BatchSocle"/> → <b>two</b> entries, not one:
///   <c>IntegrationSecrets.SftpCredentialKey</c> (the password or private key) and
///   <c>IntegrationSecrets.SftpHostKeyFingerprintKey</c>. The fingerprint is not optional and not
///   a nicety — <c>SftpFileTransport</c> REFUSES TO CONNECT without it rather than trusting
///   whatever key the server presents, and it reports exactly the same
///   <see cref="IntegrationErrors.CredentialMissing"/> that this probe does. An administrator who
///   saved the password and not the fingerprint has a connection that will never deposit a
///   bordereau, and the honest moment to tell them is on the activation screen rather than at the
///   first cut-off.</item>
/// </list>
///
/// <para>
/// A connection is therefore probed for <b>the carrier its writes would travel on</b> and not for
/// both. Demanding both would refuse an API-fed insurer for the absence of an SFTP password it
/// will never use, which is the kind of refusal that teaches an administrator to ignore the
/// activation screen.
/// </para>
/// ───────────────────────────────────────────────────────────────────────────────────────────────
///
/// <para>
/// <b>It asks the vault for a HINT and never for the value</b>, and that is a decision rather than
/// a shortcut. Nothing can be done with an ORASS credential until the specification says how it
/// travels on the wire, so a value read here would be a secret decrypted into the memory of a
/// process that has no use for it — gratuitous exposure, on a path a logger or an exception filter
/// could later widen. The hint answers exactly the question this adapter is entitled to ask: is
/// one stored. The day the specification arrives, the value read is one line
/// (<c>GetValueAsync</c> on this very key) and it belongs in the transport, next to the header it
/// fills — which is where <c>TemenosAuthenticator</c> puts it, and the reason that adapter sets
/// the credential per request instead of on <c>DefaultRequestHeaders</c> of a pooled client. The
/// SFTP side already works that way and needs no change: <c>SftpFileTransport</c> reads its own two
/// values at connection time.
/// </para>
///
/// <para>
/// <b>Why probe at all when every call refuses anyway.</b> Because "no credential is configured for
/// this connection" is a mistake the tenant's administrator can fix in a minute, while "no ORASS
/// specification and no insurer agreement" is a procurement conversation, and collapsing the two
/// sends the wrong person to the wrong screen. Probing also exercises the key shape, the scope and
/// the connection-keying today, so the day the transport is written the credential is not where the
/// mistake is.
/// </para>
///
/// <para>
/// <c>internal</c>, unlike the matrix, the carrier rule and the intermediary rule: it reaches
/// <see cref="IntegrationSecrets"/>, which the module exposes to adapter assemblies only. Keeping
/// the type internal says the vault key is the adapter's own plumbing and not something a host or
/// a screen should be computing.
/// </para>
/// </summary>
internal static class OrassCredential
{
    /// <summary>
    /// Whether everything <paramref name="carrier"/> needs is stored for this connection. Never
    /// returns, logs or otherwise materialises a value.
    /// </summary>
    /// <remarks>
    /// An expired entry counts as <see cref="OrassCredentialState.Present"/>. The hint carries
    /// <c>ExpiresAt</c>, and it is deliberately not consulted: <c>ISecretsModule.GetValueAsync</c>
    /// hands out a stored value regardless of its expiry (a shared-infrastructure gap recorded in
    /// <c>docs/integration-module-plan.md</c> §12 and deliberately not fixed there), so a probe
    /// that called an expired entry missing would report a state the transport will not see —
    /// sending an administrator to re-enter a credential that still authenticates. Expiry is the
    /// vault's business to enforce, in one place, for every module. The SAB adapter made the same
    /// alignment for the same reason, and this one follows it rather than inventing a second
    /// policy.
    /// </remarks>
    public static async Task<OrassCredentialProbe> ProbeAsync(
        ISecretsModule secrets,
        Guid tenantId,
        Guid connectionId,
        OrassCarrier carrier,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(secrets);

        if (carrier == OrassCarrier.ExternalApi)
        {
            var api = await secrets.GetHintAsync(
                IntegrationSecrets.CredentialKey(tenantId, connectionId), ct);

            return api is null
                ? new OrassCredentialProbe(OrassCredentialState.Missing, MissingApiDetail())
                : new OrassCredentialProbe(OrassCredentialState.Present, null);
        }

        var sftp = await secrets.GetHintAsync(
            IntegrationSecrets.SftpCredentialKey(tenantId, connectionId), ct);

        if (sftp is null)
            return new OrassCredentialProbe(OrassCredentialState.Missing, MissingSftpCredentialDetail());

        var fingerprint = await secrets.GetHintAsync(
            IntegrationSecrets.SftpHostKeyFingerprintKey(tenantId, connectionId), ct);

        return fingerprint is null
            ? new OrassCredentialProbe(OrassCredentialState.Missing, MissingHostKeyDetail())
            : new OrassCredentialProbe(OrassCredentialState.Present, null);
    }

    /// <summary>
    /// The API carrier's missing-credential detail, reported as
    /// <see cref="IntegrationErrors.CredentialMissing"/>.
    ///
    /// <para>
    /// It says what is missing, who fixes it, and — explicitly — that this is not the
    /// specification. Without that last clause the message is read as one more variation on
    /// "waiting for the insurer", and a connection stays broken for a reason nobody owns.
    /// </para>
    /// </summary>
    public static string MissingApiDetail()
        => "No credential is stored in the vault for this ORASS connection, and its carrier is the "
           + "insurer's API. Save it through the connection's secret endpoint (INT-03); it is held "
           + "per connection, so a credential saved for another connection of the same tenant — "
           + "including this tenant's other ORASS branch — does not serve this one. This is not the "
           + "missing ORASS specification: nothing is being waited on from "
           + $"{OrassSpecification.Counterparty} for it.";

    /// <summary>The batch carrier's first missing entry: the SFTP password or private key.</summary>
    public static string MissingSftpCredentialDetail()
        => "No SFTP credential is stored in the vault for this ORASS connection, and its carrier is "
           + "the batch socle — so there is nothing to authenticate the bordereau deposit with. "
           + "Save it through the connection's secret endpoint (INT-03). This is not the missing "
           + $"ORASS specification: nothing is being waited on from {OrassSpecification.Counterparty} "
           + "for it.";

    /// <summary>
    /// The batch carrier's second missing entry, and the one an administrator does not expect.
    /// </summary>
    public static string MissingHostKeyDetail()
        => "No SFTP host-key fingerprint is stored in the vault for this ORASS connection, and its "
           + "carrier is the batch socle. SftpFileTransport refuses to connect without it rather "
           + "than trusting whatever key the server presents, so the deposit would fail at every "
           + "cut-off: a bordereau carries an institution's customers, and a connection that "
           + "trusted any host key would hand them to whoever answered on that port. Take it with "
           + "`ssh-keyscan <host> | ssh-keygen -lf -` and save it through the connection's secret "
           + "endpoint (INT-03). This is not the missing ORASS specification: nothing is being "
           + $"waited on from {OrassSpecification.Counterparty} for it.";
}
