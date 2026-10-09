namespace Sankore.Modules.Integration.Features.RelayAgents.RecordRelayHeartbeat;

using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Sankore.Modules.Integration.Infrastructure;
using Sankore.Modules.Integration.PublicApi;
using Sankore.Shared.Kernel;

/// <summary>
/// Authenticates the heartbeat by its certificate, then records it.
///
/// <para>
/// The admission check comes first and is the same one the agent channel must run on every
/// message — not a copy of it. A revoked agent's thumbprint is cleared, so its heartbeats stop
/// being accepted the instant the revocation commits, and the operator's screen correctly shows
/// the last contact as the moment it was cut off rather than as "still alive".
/// </para>
/// </summary>
internal sealed class RecordRelayHeartbeatHandler(
    IntegrationDbContext db,
    IRelayAgentAdmission admission,
    TimeProvider clock,
    ILogger<RecordRelayHeartbeatHandler> logger)
    : IRequestHandler<RecordRelayHeartbeatCommand, Result>
{
    public async Task<Result> Handle(RecordRelayHeartbeatCommand cmd, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(cmd);

        var identity = await admission.AdmitAsync(cmd.CertificateThumbprint, ct);

        // One refusal code for an unknown certificate and for a revoked one, because revoking
        // erases the thumbprint and the two are genuinely the same state in the table.
        // RelayAgentRevoked is deliberately NOT used here: it would claim a distinction the data
        // cannot make, and claiming it to an unauthenticated caller would confirm that a
        // certificate once existed.
        if (identity is null)
            return Result.Fail(IntegrationErrors.RelayAgentNotFound);

        // IgnoreQueryFilters: there is no tenant context on this call — the tenant came out of the
        // admission check — so the global filter would compare against Guid.Empty and find
        // nothing. Paired with the agent id resolved above and its tenant, which is the pairing
        // the repo requires whenever a filter is bypassed.
        var agent = await db.RelayAgents
            .IgnoreQueryFilters()
            .AsTracking()
            .FirstOrDefaultAsync(
                a => a.Id == identity.AgentId && a.TenantId == identity.TenantId, ct);

        // Admitted a moment ago and gone now: revoked between the two reads. Refused with the
        // same code, so a race answers what a revocation answers.
        if (agent is null)
            return Result.Fail(IntegrationErrors.RelayAgentNotFound);

        // Criterion 5 is per target, so the array is stored whole and the single column keeps the
        // WORST latency — see IntegrationRelayAgent.ReportedLatencyMs for why the worst and not an
        // average. Derived here rather than in the aggregate because which number a list view
        // sorts by is a presentation choice, and the domain should not make it while pretending
        // only to store it.
        var targetsJson = SerialiseTargets(cmd.Targets);
        var highestLatency = HighestLatency(cmd.Targets) ?? cmd.LatencyMs;

        agent.RecordHeartbeat(
            cmd.Version, highestLatency, cmd.StatusDetail, targetsJson, clock);

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            // Another writer moved the row — a concurrent heartbeat, or the revocation above
            // landing between the read and the write. Reported as success: a heartbeat says "I am
            // alive, at this version", and a concurrent heartbeat from the same agent has already
            // said it. The alternative is answering 409 to a machine whose only sensible reaction
            // is to send the same thing again a second later. The reported version and latency of
            // THIS heartbeat are lost; the next one carries them.
            logger.LogDebug(
                "A heartbeat from relay agent {AgentId} lost a concurrency race and was dropped; "
                + "a newer one had already landed.", identity.AgentId);

            return Result.Ok();
        }

        // Debug, not information: this is the most frequent write in the module by a wide margin,
        // and an information line per heartbeat per agent would bury everything else in the sink.
        // The read side is where an operator looks at heartbeats.
        logger.LogDebug(
            "Heartbeat recorded for relay agent {AgentId} of tenant {TenantId} "
            + "(version {Version}, latency {LatencyMs} ms)",
            identity.AgentId, identity.TenantId, cmd.Version, cmd.LatencyMs);

        return Result.Ok();
    }

    /// <summary>
    /// camelCase, matching the agent's own wire names, so a round trip through the column and back
    /// out of the read endpoint is stable and an operator sees the field names the agent's logs use.
    /// </summary>
    private static readonly System.Text.Json.JsonSerializerOptions TargetJson = new()
    {
        PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    private static string? SerialiseTargets(IReadOnlyList<RelayTargetHealthReport>? targets)
        => targets is null or { Count: 0 }
            ? null
            : System.Text.Json.JsonSerializer.Serialize(targets, TargetJson);

    /// <summary>
    /// The highest latency reported, or null when no target carried one — an agent that has not
    /// probed anything yet reports reachability without a figure, and a zero there would read as
    /// "instantaneous" rather than "unknown".
    /// </summary>
    private static int? HighestLatency(IReadOnlyList<RelayTargetHealthReport>? targets)
    {
        if (targets is null or { Count: 0 }) return null;

        long? highest = null;

        foreach (var target in targets)
        {
            if (target.LatencyMs is not { } latency || latency < 0) continue;
            if (highest is null || latency > highest) highest = latency;
        }

        // Clamped rather than cast: the wire carries a long, the column is an int, and a
        // nonsensical figure from a misbehaving agent must not overflow into a negative one.
        return highest is null ? null : (int)Math.Min(highest.Value, int.MaxValue);
    }
}
