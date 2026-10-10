namespace Sankore.Modules.Integration.Infrastructure;

using Sankore.Shared.Kernel;

/// <summary>
/// Vault keys for the credentials of one connection (INT-03).
///
/// <para>
/// Keyed per CONNECTION and not per tenant: an IMF has a CBS and two insurers at once, each with
/// its own credentials, and a tenant-wide key would make saving the second one destroy the first.
/// M13's lead sources are keyed the same way, for the same reason.
/// </para>
///
/// <para>
/// <c>internal</c>, like M02's <c>BiometrySecrets</c>: only this module writes these and only
/// this module reads them. Should another module ever need one, move this to the PublicApi
/// rather than copying the shape.
/// </para>
/// </summary>
internal static class IntegrationSecrets
{
    public const string Scope = "integration";

    /// <summary>The credential an adapter authenticates with — OAuth secret, token, API key.</summary>
    public const string CredentialName = "connection-credential";

    /// <summary>SFTP password or private key, for a batch connection.</summary>
    public const string SftpCredentialName = "sftp-credential";

    /// <summary>
    /// SHA-256 fingerprint of the SFTP server's host key, for a batch connection (INT-24).
    ///
    /// <para>
    /// A SECOND name rather than a field on <c>BatchCapableSettings</c>, and that is not a
    /// convenience: a settings object is returned by the API, and a host key an API caller could
    /// write is a host key an API caller could replace — which turns the deposit into a
    /// man-in-the-middle away from handing a bank's customer file to whoever answers on port 22.
    /// It sits in the vault next to the credential it protects, is never returned by any endpoint,
    /// and <c>SftpFileTransport</c> REFUSES TO CONNECT when it is absent rather than trusting
    /// whatever key the server presents.
    /// </para>
    ///
    /// <para>
    /// Accepted spellings are <c>SHA256:…</c> base64 (what <c>ssh-keyscan | ssh-keygen -lf -</c>
    /// prints) and plain hex, with or without colons — see
    /// <c>SftpHostKeyFingerprint.Matches</c>. One name, so rotating a server's key is one vault
    /// write.
    /// </para>
    /// </summary>
    public const string SftpHostKeyFingerprintName = "sftp-host-key-fingerprint";

    /// <summary>HMAC secret used to verify inbound webhooks (INT-20).</summary>
    public const string WebhookSecretName = "webhook-secret";

    public static SecretKey CredentialKey(Guid tenantId, Guid connectionId)
        => new(tenantId, Scope, connectionId, CredentialName);

    public static SecretKey SftpCredentialKey(Guid tenantId, Guid connectionId)
        => new(tenantId, Scope, connectionId, SftpCredentialName);

    public static SecretKey SftpHostKeyFingerprintKey(Guid tenantId, Guid connectionId)
        => new(tenantId, Scope, connectionId, SftpHostKeyFingerprintName);

    public static SecretKey WebhookSecretKey(Guid tenantId, Guid connectionId)
        => new(tenantId, Scope, connectionId, WebhookSecretName);
}
