namespace Sankore.Modules.Integration.Features.RelayAgents.RecordRelayHeartbeat;

using MediatR;
using Sankore.Shared.Infrastructure.Behaviors;
using Sankore.Shared.Kernel;

/// <summary>
/// The agent reporting in (INT-27, criterion 3 — the ingestion half).
///
/// <para>
/// On the PUBLIC surface and authenticated by the agent's client certificate, not by a JWT: the
/// agent is a machine inside the institution's network with no user behind it. Which agent it is,
/// and which tenant it belongs to, are resolved from the certificate through
/// <see cref="IRelayAgentAdmission"/> — never taken from the body. A heartbeat that named its own
/// agent would let anybody rewrite any agent's reported state, which is what an operator reads to
/// decide whether the relay is healthy: a forged heartbeat makes a dead relay look alive, batch
/// files silently stop being deposited, and the dashboard stays green.
/// </para>
///
/// <para>
/// <see cref="ICommand"/> for criterion 4's "all actions are audited", like every other write in
/// this folder. Worth knowing what that costs: one audit row per heartbeat per agent, so the
/// heartbeat PERIOD — which the agent chooses, not this module — decides the volume. That is a
/// deployment knob and an audit-retention question, not a reason to drop the marker: dropping it
/// would also drop the transaction, and silently, which is the failure mode
/// <c>HumanActionsAreAuditedTests</c> exists to prevent.
/// </para>
/// </summary>
/// <param name="CertificateThumbprint">
/// SHA-256 fingerprint, 64 lower-case hexadecimal characters, of the certificate presented in the
/// TLS handshake.
/// <para>
/// <b>SERVER-SET ONLY.</b> <see cref="RecordRelayHeartbeatEndpoint"/> computes it from
/// <c>HttpContext.Connection</c>; <see cref="RecordRelayHeartbeatRequest"/> has no such field and
/// a test pins that it never gains one. A thumbprint is a hash of a <i>public</i> certificate —
/// reproducible by anyone who has seen it — so one taken from a payload authenticates nobody, and
/// trusting it would let any caller post a heartbeat for any agent.
/// </para>
/// <para>
/// Marked sensitive for the audit trail all the same: not a secret, but the identifier the channel
/// authenticates on, and <c>audit.entries</c> is append-only and widely readable. Same reason the
/// read side never returns it.
/// </para>
/// </param>
/// <param name="Version">
/// What the agent says it is running, so a site left on a stale build is visible from the
/// platform rather than discovered during an incident.
/// </param>
/// <param name="LatencyMs">
/// Latency the agent measured towards the systems it relays. Its own value and its own
/// measurement — our view of the agent is the call journal's business (INT-08).
/// </param>
/// <param name="StatusDetail">
/// A short line the agent writes about itself. Never personal data: this is a machine describing
/// its own health, and it is shown verbatim on an operator's screen.
/// </param>
internal sealed record RecordRelayHeartbeatCommand(
    [property: SensitiveData] string CertificateThumbprint,
    string? Version,
    int? LatencyMs,
    string? StatusDetail,
    IReadOnlyList<RelayTargetHealthReport>? Targets = null)
    : IRequest<Result>, ICommand, IResourceCommand
{
    public string ResourceType => "IntegrationRelayAgent";

    /// <summary>
    /// Null: the agent is identified by its certificate, and resolving it is the handler's first
    /// act. An id in the body is the exact thing §5bis forbids.
    /// </summary>
    public string? ResourceId => null;
}
