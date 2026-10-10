namespace Sankore.Modules.Integration.Infrastructure.Resilience;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Polly.CircuitBreaker;

/// <summary>
/// The module's health check, registered as <c>"integration"</c> (INT-09, criterion 4). Reports,
/// per active connection, the circuit-breaker state and the last stored health result.
///
/// <para>
/// <b>Two facts worth knowing before concluding it is missing.</b> First, this is the FIRST named
/// health check in this repository: <c>ServiceDefaults.AddServiceDefaults</c> calls
/// <c>AddHealthChecks()</c> and until now nothing contributed a check to it, so the aggregate
/// report has always been an empty <c>Healthy</c>. Second,
/// <c>Sankore.Api/Infrastructure/ServiceDefaults.cs</c> maps the detailed <c>/health</c> endpoint
/// <b>only in Development</b> — in production there is no route serving the per-check breakdown at
/// all. A bare or absent production <c>/health</c> is therefore not evidence that this check is
/// not running; it is evidence that nothing exposes it yet. Reading the breaker state in
/// production needs either that mapping lifted out of the Development guard or an endpoint of this
/// module's own, and neither is in this slice's hands.
/// </para>
///
/// <para>
/// <b>It calls nothing.</b> A health probe runs on a timer, on every instance, and a probe that
/// reached out to a core banking system would turn a Kubernetes liveness interval into load on a
/// bank — and would open the very breaker it is meant to observe. So the external-facing half is
/// read from <c>IntegrationConnection.LastHealthStatus</c>, written by the connection's own health
/// endpoint (INT-03), and the breaker half from the in-process state provider. Both are already
/// known; neither costs a call.
/// </para>
///
/// <para>
/// <b>It spans tenants, on purpose.</b> <c>/health</c> carries no JWT, so there is no ambient
/// tenant to scope to, and an operator's question is "is any connection down", not "is tenant
/// X's". The query therefore uses <c>IgnoreQueryFilters()</c>; what it publishes per row is the
/// tenant id, the connection id, the kind and the two states — never a setting, never a vault
/// reference, never a credential.
/// </para>
///
/// <para>
/// Severity: an <see cref="CircuitState.Open"/> or <see cref="CircuitState.Isolated"/> circuit is
/// <b>Unhealthy</b> — writes to that CBS are being refused right now. A half-open circuit, or a
/// connection whose last stored check failed, is <b>Degraded</b>: something is wrong but the
/// platform is still functioning, because a command that cannot be sent is queued rather than
/// lost. The module is never Unhealthy merely for having no connection configured — an IMF that
/// has not bought a CBS link is not a broken deployment.
/// </para>
/// </summary>
internal sealed class IntegrationHealthCheck(
    IntegrationDbContext db,
    IntegrationResiliencePipelineProvider pipelines) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        var connections = await db.Connections
            .IgnoreQueryFilters()
            .Where(c => c.IsActive)
            .Select(c => new
            {
                c.Id,
                c.TenantId,
                c.Family,
                c.Kind,
                c.Mode,
                c.LastHealthAt,
                c.LastHealthStatus,
            })
            .ToListAsync(cancellationToken);

        if (connections.Count == 0)
        {
            return HealthCheckResult.Healthy(
                "No active integration connection is configured.");
        }

        var data = new Dictionary<string, object>(connections.Count);
        var openCircuits = 0;
        var degraded = 0;

        foreach (var connection in connections)
        {
            // Null means no pipeline has been built for this connection in THIS process — no call
            // has gone out since the last restart. Reported as "unknown" rather than "closed": a
            // breaker that has never run is not evidence of health, and a reader who sees
            // "Closed" would conclude the opposite.
            var circuit = pipelines.GetCircuitState(connection.Id);

            if (circuit is CircuitState.Open or CircuitState.Isolated) openCircuits++;
            else if (circuit is CircuitState.HalfOpen) degraded++;

            if (connection.LastHealthStatus == false) degraded++;

            data[$"connection:{connection.Id}"] = new
            {
                tenantId = connection.TenantId,
                family = connection.Family.ToString(),
                kind = connection.Kind.ToString(),
                mode = connection.Mode.ToString(),
                circuit = circuit?.ToString() ?? "Unknown",
                // Tri-state and kept tri-state: false is a failed check, null is "never ran".
                lastHealthStatus = connection.LastHealthStatus,
                lastHealthAt = connection.LastHealthAt,
            };
        }

        var description =
            $"{connections.Count} active connection(s); {openCircuits} with an open circuit, "
            + $"{degraded} degraded.";

        if (openCircuits > 0) return HealthCheckResult.Unhealthy(description, data: data);
        if (degraded > 0) return HealthCheckResult.Degraded(description, data: data);

        return HealthCheckResult.Healthy(description, data);
    }
}
