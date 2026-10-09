namespace Sankore.Modules.Integration.Features.RelayAgents;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Sankore.Modules.Integration.Domain;
using Sankore.Modules.Integration.Infrastructure;

/// <summary>
/// Who an agent is, once its certificate has been admitted. Everything the caller needs to decide
/// what the agent may do, and nothing else: no thumbprint, no token, no status history.
/// </summary>
/// <param name="AgentId">The row in <c>integration_relay_agent</c>.</param>
/// <param name="TenantId">
/// The tenant the agent belongs to — <b>established here and never taken from the request</b>.
/// This is the whole point of the enrolment flow (docs/integration-module-plan.md §5bis a): an
/// agent id or a tenant id accepted from a caller would let one tenant route its writes through
/// another tenant's on-premise network.
/// </param>
/// <param name="Name">The operator-facing name, for logs and for an operator's screen.</param>
public sealed record RelayAgentIdentity(Guid AgentId, Guid TenantId, string Name);

/// <summary>
/// The authoritative admission check for a relay agent's session (INT-27, criterion 2).
///
/// <para>
/// ──────────────────────────────────────────────────────────────────────────────────────────<br/>
/// <b>THE CHANNEL OWNER MUST CALL THIS ON EVERY MESSAGE.</b> Criterion 2 says a revocation cuts
/// the session <i>immediately</i>, and nothing in this module can close a live socket: the socket
/// belongs to whatever hosts the agent channel (INT-26). Revoking clears the certificate
/// thumbprint, so from the instant the revocation commits this method answers <c>null</c> for
/// that certificate — but only for a caller that asks. A channel that authenticates once at
/// connect time and then trusts the connection turns criterion 2 into "the revocation stops the
/// NEXT connection", which is not what the criterion says.
/// </para>
/// <para>
/// Per message and not per connection, and deliberately not cached: a cache is a window during
/// which a revoked agent is still admitted, and the window's length is the part nobody remembers
/// when reading the criterion. The check is one indexed equality on
/// <c>ux_integration_relay_agent_certificate</c>.
/// </para>
/// <para>
/// ──────────────────────────────────────────────────────────────────────────────────────────
/// </para>
///
/// <para>
/// Public, while the rest of this folder is internal, because the channel that has to call it is
/// not in this assembly and does not exist yet. An internal interface would make criterion 2
/// undeliverable by construction.
/// </para>
/// </summary>
public interface IRelayAgentAdmission
{
    /// <summary>
    /// The identity behind a certificate thumbprint, or <c>null</c> when that certificate is not
    /// admitted — unknown, never enrolled, or revoked.
    ///
    /// <para>
    /// ──────────────────────────────────────────────────────────────────────────────────────<br/>
    /// <b>THE THUMBPRINT MUST COME FROM A CERTIFICATE WHOSE PRIVATE KEY WAS PROVEN IN A TLS
    /// HANDSHAKE — never from a payload, a header or a query string.</b> Compute it from the
    /// connection, as <c>RecordRelayHeartbeatEndpoint</c> does:
    /// <c>Convert.ToHexStringLower(cert.GetCertHash(HashAlgorithmName.SHA256))</c> over
    /// <c>HttpContext.Connection.GetClientCertificateAsync()</c>, and refuse when there is no
    /// certificate. A thumbprint is a hash of a <i>public</i> certificate: anyone who has ever
    /// seen the certificate can reproduce it, so a thumbprint taken from the wire authenticates
    /// nobody and this method would cheerfully hand back a real identity for it. That is not a
    /// caveat about one endpoint — hand this method a body-supplied value and the channel's
    /// entire authentication collapses with it, including the per-message re-check that
    /// criterion 2 rests on.
    /// </para>
    /// <para>
    /// ──────────────────────────────────────────────────────────────────────────────────────
    /// </para>
    ///
    /// <para>
    /// One answer for all three, because the data cannot tell them apart and should not: revoking
    /// erases the thumbprint, so a revoked agent's certificate and a certificate we have never
    /// seen are literally the same state in the table. That is also why no reason code is
    /// returned — a channel that could report "revoked" rather than "unknown" would be telling an
    /// unauthenticated caller which certificates once existed.
    /// </para>
    /// </summary>
    Task<RelayAgentIdentity?> AdmitAsync(string? certificateThumbprint, CancellationToken ct);
}

internal sealed class RelayAgentAdmission(
    IntegrationDbContext db,
    ILogger<RelayAgentAdmission> logger) : IRelayAgentAdmission
{
    public async Task<RelayAgentIdentity?> AdmitAsync(
        string? certificateThumbprint, CancellationToken ct)
    {
        // A blank thumbprint is refused before it reaches the database: `null == null` would match
        // every row whose certificate has been cleared, which is every revoked and every pending
        // agent on the platform. The query below is filtered on Active as well, so this is belt
        // and braces — and it is the belt that would still be there if someone relaxed the status
        // filter while chasing a bug.
        if (string.IsNullOrWhiteSpace(certificateThumbprint)) return null;

        var thumbprint = RelayCertificateThumbprint.Normalize(certificateThumbprint);

        // IgnoreQueryFilters is MANDATORY here and not an optimisation: an agent presenting a
        // certificate carries no JWT and no tenant header, so the global query filter would
        // compare TenantId against Guid.Empty and find nothing — every agent on the platform
        // would be refused, with nothing in the logs to say why. The tenant is an OUTPUT of this
        // method, which is exactly the inversion §5bis asks for.
        //
        // Scoping is not lost by doing so: the thumbprint is unique across the table
        // (ux_integration_relay_agent_certificate), one agent belongs to one tenant, and the
        // tenant travels back in the identity so the caller can enforce
        // agent.TenantId == connection.TenantId — §5ter's second obligation, which belongs to the
        // dispatcher and not here.
        var identity = await db.RelayAgents
            .IgnoreQueryFilters()
            .Where(a => a.CertificateThumbprint == thumbprint
                        && a.Status == RelayAgentStatus.Active)
            .Select(a => new RelayAgentIdentity(a.Id, a.TenantId, a.Name))
            .FirstOrDefaultAsync(ct);

        if (identity is null)
        {
            // Warning, not information: on a healthy installation this never happens, and when it
            // does it is either a revoked agent still trying to talk to us or somebody presenting
            // a certificate we never issued. Neither the thumbprint nor any part of it is logged —
            // it is not a secret, but it is the identifier a channel authenticates on, so a log
            // sink chosen for volume is not where it belongs.
            logger.LogWarning(
                "A relay agent certificate was refused: it is unknown, not yet enrolled, or revoked.");

            return null;
        }

        return identity;
    }
}
