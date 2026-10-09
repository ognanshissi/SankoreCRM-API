namespace Sankore.Modules.Integration.Infrastructure.Transport;

/// <summary>
/// Platform-level egress policy for connections this process opens ITSELF, bound from
/// <c>Integration:Egress</c>.
///
/// <para>
/// <b>Platform-level, and deliberately not a tenant setting.</b> The whole shape of the
/// vulnerability this guards is that <c>SftpHost</c> and <c>SftpPort</c> live in a connection's
/// settings, which a tenant administrator edits, while the network position and the credential
/// used to reach them are SANKORE's. A switch a tenant could flip would be the vulnerability with
/// an extra step.
/// </para>
/// </summary>
internal sealed class IntegrationEgressOptions
{
    public const string SectionName = "Integration:Egress";

    /// <summary>
    /// Permits a direct connection to a loopback, private, link-local or CGNAT address.
    /// <b>False by default, and it must stay that way on any hosted deployment.</b>
    ///
    /// <para>
    /// It exists for the single-tenant, self-hosted installation whose CBS genuinely does sit on
    /// the same private network as SANKORE — there, a private target is the intended one. On a
    /// multi-tenant host it is the opposite: a private address seen from this process is
    /// SANKORE's OWN network, never the IMF's, so honouring it would deposit one tenant's
    /// customer file against an internal service of ours. An on-premise system is unreachable
    /// from here by construction, which is precisely why <c>IntegrationMode</c> has three values
    /// and why the relay agent (INT-26) exists: <see cref="Sankore.Modules.Integration.PublicApi.IntegrationMode.Relay"/>
    /// is the sanctioned route to a private target, and it opens no socket from this process at
    /// all.
    /// </para>
    ///
    /// <para>
    /// Turning it on logs a warning naming this setting at start-up, the repo's pattern for a
    /// guard that has been deliberately loosened — an invisible relaxation is the kind nobody
    /// remembers two deployments later.
    /// </para>
    /// </summary>
    public bool AllowPrivateAddresses { get; set; }
}
