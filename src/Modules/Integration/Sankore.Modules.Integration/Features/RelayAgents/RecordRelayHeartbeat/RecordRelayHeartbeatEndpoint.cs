namespace Sankore.Modules.Integration.Features.RelayAgents.RecordRelayHeartbeat;

using MediatR;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Sankore.Modules.Integration.Features.RelayAgents.ExchangeEnrolmentToken;
using Sankore.Modules.Integration.PublicApi;

/// <summary>
/// The agent's heartbeat (INT-27, criterion 3 — the ingestion half). PUBLIC surface: the caller
/// is the relay agent itself, which holds a client certificate and no JWT.
///
/// <para>
/// ───────────────────────────────────────────────────────────────────────────────────────────<br/>
/// <b>THE THUMBPRINT IS COMPUTED HERE, FROM THE TLS HANDSHAKE. IT IS NEVER READ FROM THE
/// BODY.</b> A thumbprint is a hash of a <i>public</i> certificate: anybody who has ever seen the
/// certificate — or read it off a log, a screen or a config file — can reproduce it. Accepting one
/// in a payload would therefore let any caller who can reach this route post a heartbeat for any
/// agent, and the consequence is worse than it first looks: a forged heartbeat makes a <b>dead
/// relay look alive</b>. Batch files silently stop being deposited, nothing is relayed into the
/// institution's network, and the operations dashboard stays green — strictly worse than a relay
/// that is visibly down. Only the private key proves identity, and the only place that proof
/// happens is the handshake.
/// </para>
/// <para>
/// ───────────────────────────────────────────────────────────────────────────────────────────
/// </para>
///
/// <para>
/// <c>204</c> when recorded, <c>401</c> otherwise — with one code and no detail, for all three
/// refusals: no client certificate presented, a certificate that is not admitted, and a revoked
/// agent (whose thumbprint has been erased, so it is the same state as an unknown one). An
/// unauthenticated caller must not learn <i>which</i> of the three happened; in particular it must
/// not be told that mutual TLS is not configured, which would be an invitation.
/// </para>
///
/// <para>
/// <b>Until client certificates are enabled on the route this endpoint refuses everything, and
/// that is the correct failure mode</b> — see <see cref="RelayCertificateThumbprint"/> for the
/// infrastructure decision this US cannot settle. A relay that cannot report in is visible; a
/// relay anybody can impersonate is not.
/// </para>
/// </summary>
internal static class RecordRelayHeartbeatEndpoint
{
    internal static IEndpointRouteBuilder MapRecordRelayHeartbeat(this IEndpointRouteBuilder app)
    {
        app.MapPost("heartbeat", Handle)
            // Anonymous in the sense that there is no JWT and no tenant header. The caller is
            // still authenticated — by its client certificate, in the handshake, below.
            .AllowAnonymous()
            // The module's existing public-ingest policy: 60 a minute per remote IP, shared with
            // the enrolment route and the webhook. Reused rather than invented — a policy name
            // the host does not declare is no limit at all, silently. It bounds a misbehaving or
            // hostile caller; it is not what authorises the call.
            .RequireRateLimiting("integration-webhook")
            .WithName("RecordIntegrationRelayHeartbeat")
            .WithTags("Integration")
            .WithSummary("Record a relay agent's heartbeat")
            .WithDescription(
                "Called by the on-premise relay agent to report that it is alive, which version "
                + "it runs and what latency it measures towards the systems it relays. "
                + "Authenticated by the CLIENT CERTIFICATE presented in the TLS handshake: the "
                + "platform computes its SHA-256 thumbprint itself and matches it against the "
                + "certificate the agent was admitted with. The body carries no identity at all — "
                + "a thumbprint is a hash of a public certificate, so trusting one from a payload "
                + "would let anyone make a dead relay look alive. A call with no client "
                + "certificate, with one that is not admitted, or from a revoked agent all answer "
                + "the same 401 with no detail. What was reported is readable by an operator "
                + "through GET api/v1/integration/relay-agents. Audited.")
            .Produces(StatusCodes.Status204NoContent)
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status422UnprocessableEntity)
            .WithOpenApi();

        return app;
    }

    private static async Task<IResult> Handle(
        RecordRelayHeartbeatRequest req,
        HttpContext http,
        ISender sender,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(req);
        ArgumentNullException.ThrowIfNull(http);

        // The same computation the enrolment route uses, from the same place — one method, so the
        // value recorded at enrolment and the value checked here cannot drift apart.
        var thumbprint = await RelayClientCertificate.ThumbprintAsync(http);

        // Fail closed. No certificate means no proof of a private key, and there is nothing else
        // on this request that could identify an agent. Refused with the same code and the same
        // empty answer as an unknown certificate, so the route says nothing about whether mutual
        // TLS is configured on this deployment.
        if (thumbprint is null)
            return Refused();

        var result = await sender.Send(
            new RecordRelayHeartbeatCommand(
                thumbprint, req.Version, req.LatencyMs, req.StatusDetail, req.Targets),
            ct);

        return result.IsSuccess ? Results.NoContent() : Refused();
    }

    /// <summary>
    /// One refusal, one code, no detail — so no future edit can tell the three cases apart on the
    /// wire.
    /// </summary>
    private static IResult Refused()
        => Results.Json(
            new { error = IntegrationErrors.RelayAgentNotFound },
            statusCode: StatusCodes.Status401Unauthorized);
}

/// <summary>
/// What the agent says <b>about itself</b> — and nothing that says <b>who</b> it is.
///
/// <para>
/// Deliberately three fields. No thumbprint, no agent id, no tenant id: identity comes from the
/// TLS handshake, which is the only place a private key is proven, and
/// <c>HeartbeatDoesNotTrustTheBodyTests</c> pins the absence. The values here are reported state
/// shown on an operator's screen; they are what the agent claims, never what authorises it.
/// </para>
/// </summary>
internal sealed record RecordRelayHeartbeatRequest(
    string? Version,
    int? LatencyMs,
    string? StatusDetail,
    IReadOnlyList<RelayTargetHealthReport>? Targets = null);

/// <summary>
/// Reachability and latency of ONE relayed system, as the agent reports it (INT-26 criterion 5:
/// "la latence vers chaque système relié").
///
/// <para>
/// It mirrors the agent's own <c>RelayTargetHealth</c> field for field, and that mirroring is the
/// thing to preserve: the two records are in different assemblies that never reference each other
/// — the agent runs on the IMF's hardware and knows nothing of this module — so a rename on one
/// side deserialises to nulls on the other. M02's biometry client is the cautionary tale.
/// </para>
///
/// <para>
/// <see cref="Name"/> and <see cref="Kind"/> are the DECLARED target's name and kind from the
/// agent's own configuration — never a URL, a host or a credential. This payload is stored and
/// returned to an operator.
/// </para>
/// </summary>
internal sealed record RelayTargetHealthReport(
    string Name,
    string? Kind,
    string? State,
    long? LatencyMs,
    DateTimeOffset? CheckedAt);
